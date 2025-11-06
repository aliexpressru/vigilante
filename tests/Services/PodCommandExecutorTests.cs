using k8s;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using System.Net.WebSockets;
using System.Text;
using Vigilante.Services;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class PodCommandExecutorTests
{
    private IKubernetes _kubernetes = null!;
    private ILogger<PodCommandExecutor> _logger = null!;
    private PodCommandExecutor _executor = null!;

    [SetUp]
    public void Setup()
    {
        _kubernetes = Substitute.For<IKubernetes>();
        _logger = Substitute.For<ILogger<PodCommandExecutor>>();
        _executor = new PodCommandExecutor(_kubernetes, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _kubernetes.Dispose();
    }

    #region ListDirectoriesAsync Tests

    [Test]
    public async Task ListDirectoriesAsync_ShouldParseDirectoryNamesCorrectly()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "default";
        var directory = "/qdrant/storage/collections";
        
        // Simulating output from: cd /qdrant/storage/collections && ls -1d */
        // Expected format: each directory name on a new line with trailing slash
        var mockOutput = "test_collection/\nproducts/\nembeddings/\n";
        
        var mockWebSocket = CreateMockWebSocket(mockOutput);
        _kubernetes.WebSocketNamespacedPodExecAsync(
            podName, 
            podNamespace, 
            Arg.Is<IEnumerable<string>>(args => args.ElementAt(2).Contains("ls -1d */")),
            "qdrant",
            cancellationToken: Arg.Any<CancellationToken>()
        ).Returns(mockWebSocket);

        // Act
        var result = await _executor.ListDirectoriesAsync(podName, podNamespace, directory, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0], Is.EqualTo("test_collection"));
        Assert.That(result[1], Is.EqualTo("products"));
        Assert.That(result[2], Is.EqualTo("embeddings"));
    }

    [Test]
    public async Task ListDirectoriesAsync_ShouldFilterOutHiddenDirectories()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "default";
        var directory = "/qdrant/storage";
        
        // Output includes hidden directories (starting with .)
        var mockOutput = ".hidden/\nvisible/\n.git/\ndata/\n";
        
        var mockWebSocket = CreateMockWebSocket(mockOutput);
        _kubernetes.WebSocketNamespacedPodExecAsync(
            Arg.Any<string>(), 
            Arg.Any<string>(), 
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string>(),
            cancellationToken: Arg.Any<CancellationToken>()
        ).Returns(mockWebSocket);

        // Act
        var result = await _executor.ListDirectoriesAsync(podName, podNamespace, directory, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Does.Not.Contain(".hidden"));
        Assert.That(result, Does.Not.Contain(".git"));
        Assert.That(result, Does.Contain("visible"));
        Assert.That(result, Does.Contain("data"));
    }

    [Test]
    public async Task ListDirectoriesAsync_ShouldHandleEmptyOutput()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "default";
        var directory = "/empty/directory";
        
        var mockOutput = "";
        
        var mockWebSocket = CreateMockWebSocket(mockOutput);
        _kubernetes.WebSocketNamespacedPodExecAsync(
            Arg.Any<string>(), 
            Arg.Any<string>(), 
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string>(),
            cancellationToken: Arg.Any<CancellationToken>()
        ).Returns(mockWebSocket);

        // Act
        var result = await _executor.ListDirectoriesAsync(podName, podNamespace, directory, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task ListDirectoriesAsync_ShouldRemoveControlCharacters()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "default";
        var directory = "/qdrant/storage";
        
        // Output with carriage returns and extra whitespace
        // The parsing code removes control chars, so "collection1/\r" becomes "collection1/"
        var mockOutput = "collection1/\r\ncollection2/\r\ntest_data/\n";
        
        var mockWebSocket = CreateMockWebSocket(mockOutput);
        _kubernetes.WebSocketNamespacedPodExecAsync(
            Arg.Any<string>(), 
            Arg.Any<string>(), 
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string>(),
            cancellationToken: Arg.Any<CancellationToken>()
        ).Returns(mockWebSocket);

        // Act
        var result = await _executor.ListDirectoriesAsync(podName, podNamespace, directory, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0], Is.EqualTo("collection1"));
        Assert.That(result[1], Is.EqualTo("collection2"));
        Assert.That(result[2], Is.EqualTo("test_data"));
        // Verify no control characters in results
        foreach (var item in result)
        {
            Assert.That(item.All(c => !char.IsControl(c)), Is.True, $"Item '{item}' contains control characters");
        }
    }

    #endregion

    #region ListFilesAsync Tests

    [Test]
    public async Task ListFilesAsync_ShouldParseSnapshotFilesCorrectly()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "default";
        var directory = "/qdrant/storage/snapshots/test_collection";
        var pattern = "*.snapshot";
        
        // Simulating output from: cd /qdrant/storage/snapshots/test_collection && ls -1 *.snapshot 2>/dev/null || echo ''
        var mockOutput = "test_collection-2024-11-01-120000.snapshot\ntest_collection-2024-11-05-093000.snapshot\n";
        
        var mockWebSocket = CreateMockWebSocket(mockOutput);
        _kubernetes.WebSocketNamespacedPodExecAsync(
            podName, 
            podNamespace, 
             Arg.Any<IEnumerable<string>>(),
            "qdrant",
            cancellationToken: Arg.Any<CancellationToken>()
        ).Returns(mockWebSocket);

        // Act
        var result = await _executor.ListFilesAsync(podName, podNamespace, directory, pattern, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0], Is.EqualTo("test_collection-2024-11-01-120000.snapshot"));
        Assert.That(result[1], Is.EqualTo("test_collection-2024-11-05-093000.snapshot"));
    }

    [Test]
    public async Task ListFilesAsync_ShouldHandleNoMatchingFiles()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "default";
        var directory = "/qdrant/storage/snapshots/empty_collection";
        var pattern = "*.snapshot";
        
        // Command returns empty string when no files match
        var mockOutput = "";
        
        var mockWebSocket = CreateMockWebSocket(mockOutput);
        _kubernetes.WebSocketNamespacedPodExecAsync(
            Arg.Any<string>(), 
            Arg.Any<string>(), 
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string>(),
            cancellationToken: Arg.Any<CancellationToken>()
        ).Returns(mockWebSocket);

        // Act
        var result = await _executor.ListFilesAsync(podName, podNamespace, directory, pattern, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task ListFilesAsync_ShouldHandleDirectoryPattern()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "default";
        var directory = "/qdrant/storage/snapshots";
        var pattern = "*/";
        
        // Listing collection directories in snapshots folder
        var mockOutput = "collection1/\ncollection2/\ncollection3/\n";
        
        var mockWebSocket = CreateMockWebSocket(mockOutput);
        _kubernetes.WebSocketNamespacedPodExecAsync(
            Arg.Any<string>(), 
            Arg.Any<string>(), 
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string>(),
            cancellationToken: Arg.Any<CancellationToken>()
        ).Returns(mockWebSocket);

        // Act
        var result = await _executor.ListFilesAsync(podName, podNamespace, directory, pattern, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0], Is.EqualTo("collection1"));
        Assert.That(result[1], Is.EqualTo("collection2"));
        Assert.That(result[2], Is.EqualTo("collection3"));
    }

    #endregion

    #region GetSizeAsync Tests

    [Test]
    public async Task GetSizeAsync_ShouldParseValidSizeInBytes()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "default";
        var baseDirectory = "/qdrant/storage/collections";
        var itemName = "test_collection";
        
        // Simulating output from: cd /qdrant/storage/collections && du -sb "test_collection" | cut -f1
        // Expected: just the size in bytes
        var mockOutput = "1288490188\n";
        
        var mockWebSocket = CreateMockWebSocket(mockOutput);
        _kubernetes.WebSocketNamespacedPodExecAsync(
            podName, 
            podNamespace, 
             Arg.Any<IEnumerable<string>>(),
            "qdrant",
            cancellationToken: Arg.Any<CancellationToken>()
        ).Returns(mockWebSocket);

        // Act
        var result = await _executor.GetSizeAsync(podName, podNamespace, baseDirectory, itemName, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Value, Is.EqualTo(1288490188L));
    }

    [Test]
    public async Task GetSizeAsync_ShouldHandleSmallSizes()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "default";
        var baseDirectory = "/qdrant/storage/snapshots/test";
        var itemName = "small.snapshot";
        
        // Small file size
        var mockOutput = "1024\n";
        
        var mockWebSocket = CreateMockWebSocket(mockOutput);
        _kubernetes.WebSocketNamespacedPodExecAsync(
            Arg.Any<string>(), 
            Arg.Any<string>(), 
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string>(),
            cancellationToken: Arg.Any<CancellationToken>()
        ).Returns(mockWebSocket);

        // Act
        var result = await _executor.GetSizeAsync(podName, podNamespace, baseDirectory, itemName, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Value, Is.EqualTo(1024L));
    }

    [Test]
    public async Task GetSizeAsync_ShouldHandleLargeSizes()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "default";
        var baseDirectory = "/qdrant/storage/collections";
        var itemName = "huge_collection";
        
        // Very large collection (10 TB)
        var mockOutput = "10995116277760\n";
        
        var mockWebSocket = CreateMockWebSocket(mockOutput);
        _kubernetes.WebSocketNamespacedPodExecAsync(
            Arg.Any<string>(), 
            Arg.Any<string>(), 
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string>(),
            cancellationToken: Arg.Any<CancellationToken>()
        ).Returns(mockWebSocket);

        // Act
        var result = await _executor.GetSizeAsync(podName, podNamespace, baseDirectory, itemName, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Value, Is.EqualTo(10995116277760L));
    }

    [Test]
    public async Task GetSizeAsync_ShouldHandleOutputWithControlCharacters()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "default";
        var baseDirectory = "/qdrant/storage/collections";
        var itemName = "test_collection";
        
        // Output with control characters, carriage returns, null bytes
        var mockOutput = "524288000\r\n\x00";
        
        var mockWebSocket = CreateMockWebSocket(mockOutput);
        _kubernetes.WebSocketNamespacedPodExecAsync(
            Arg.Any<string>(), 
            Arg.Any<string>(), 
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string>(),
            cancellationToken: Arg.Any<CancellationToken>()
        ).Returns(mockWebSocket);

        // Act
        var result = await _executor.GetSizeAsync(podName, podNamespace, baseDirectory, itemName, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Value, Is.EqualTo(524288000L));
    }

    [Test]
    public async Task GetSizeAsync_ShouldReturnNullForInvalidOutput()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "default";
        var baseDirectory = "/qdrant/storage/collections";
        var itemName = "invalid_item";
        
        // Invalid output (not a number)
        var mockOutput = "du: cannot access 'invalid_item': No such file or directory\n";
        
        var mockWebSocket = CreateMockWebSocket(mockOutput);
        _kubernetes.WebSocketNamespacedPodExecAsync(
            Arg.Any<string>(), 
            Arg.Any<string>(), 
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string>(),
            cancellationToken: Arg.Any<CancellationToken>()
        ).Returns(mockWebSocket);

        // Act
        var result = await _executor.GetSizeAsync(podName, podNamespace, baseDirectory, itemName, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetSizeAsync_ShouldReturnNullForEmptyOutput()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "default";
        var baseDirectory = "/qdrant/storage/collections";
        var itemName = "empty_item";
        
        var mockOutput = "";
        
        var mockWebSocket = CreateMockWebSocket(mockOutput);
        _kubernetes.WebSocketNamespacedPodExecAsync(
            Arg.Any<string>(), 
            Arg.Any<string>(), 
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string>(),
            cancellationToken: Arg.Any<CancellationToken>()
        ).Returns(mockWebSocket);

        // Act
        var result = await _executor.GetSizeAsync(podName, podNamespace, baseDirectory, itemName, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetSizeAsync_ShouldExtractNumbersFromMixedOutput()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "default";
        var baseDirectory = "/qdrant/storage/collections";
        var itemName = "test_collection";
        
        // Output with extra characters (should extract only digits)
        var mockOutput = "Size: 1073741824 bytes\n";
        
        var mockWebSocket = CreateMockWebSocket(mockOutput);
        _kubernetes.WebSocketNamespacedPodExecAsync(
            Arg.Any<string>(), 
            Arg.Any<string>(), 
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string>(),
            cancellationToken: Arg.Any<CancellationToken>()
        ).Returns(mockWebSocket);

        // Act
        var result = await _executor.GetSizeAsync(podName, podNamespace, baseDirectory, itemName, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Value, Is.EqualTo(1073741824L));
    }

    #endregion

    #region DeleteAndVerifyAsync Tests

    [Test]
    public async Task DeleteAndVerifyAsync_ShouldSuccessfullyDeleteDirectory()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "default";
        var fullPath = "/qdrant/storage/collections/test_collection";
        var isDirectory = true;
        var description = "Collection test_collection";
        
        // Mock delete command (rm -rf) - no output on success
        var deleteWebSocket = CreateMockWebSocket("");
        
        // Mock verify command - should return 'deleted'
        var verifyWebSocket = CreateMockWebSocket("deleted\n");
        
        // Setup mocks to return different results on subsequent calls
        _kubernetes.WebSocketNamespacedPodExecAsync(
            podName,
            podNamespace,
            Arg.Any<IEnumerable<string>>(),
            "qdrant",
            cancellationToken: Arg.Any<CancellationToken>()
        ).Returns(deleteWebSocket, verifyWebSocket);

        // Act
        var result = await _executor.DeleteAndVerifyAsync(
            podName, podNamespace, fullPath, isDirectory, description, CancellationToken.None);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task DeleteAndVerifyAsync_ShouldSuccessfullyDeleteFile()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "default";
        var fullPath = "/qdrant/storage/snapshots/test/snapshot.snapshot";
        var isDirectory = false;
        var description = "Snapshot snapshot.snapshot";
        
        // Mock delete command (rm -f)
        var deleteWebSocket = CreateMockWebSocket("");
        
        // Mock verify command for file
        var verifyWebSocket = CreateMockWebSocket("deleted\n");
        
        // Setup mocks to return different results on subsequent calls
        _kubernetes.WebSocketNamespacedPodExecAsync(
            podName,
            podNamespace,
            Arg.Any<IEnumerable<string>>(),
            "qdrant",
            cancellationToken: Arg.Any<CancellationToken>()
        ).Returns(deleteWebSocket, verifyWebSocket);

        // Act
        var result = await _executor.DeleteAndVerifyAsync(
            podName, podNamespace, fullPath, isDirectory, description, CancellationToken.None);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task DeleteAndVerifyAsync_ShouldFailWhenDeleteReturnsError()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "default";
        var fullPath = "/qdrant/storage/collections/protected";
        var isDirectory = true;
        var description = "Collection protected";
        
        // Mock delete command returning permission error
        var deleteWebSocket = CreateMockWebSocket("rm: cannot remove '/qdrant/storage/collections/protected': Permission denied\n");
        
        _kubernetes.WebSocketNamespacedPodExecAsync(
            podName,
            podNamespace,
             Arg.Any<IEnumerable<string>>(),
            "qdrant",
            cancellationToken: Arg.Any<CancellationToken>()
        ).Returns(deleteWebSocket);

        // Act
        var result = await _executor.DeleteAndVerifyAsync(
            podName, podNamespace, fullPath, isDirectory, description, CancellationToken.None);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task DeleteAndVerifyAsync_ShouldFailWhenItemStillExists()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "default";
        var fullPath = "/qdrant/storage/collections/stubborn";
        var isDirectory = true;
        var description = "Collection stubborn";
        
        // Delete succeeds
        var deleteWebSocket = CreateMockWebSocket("");
        
        // But verification shows it still exists
        var verifyWebSocket = CreateMockWebSocket("exists\n");
        
        // Setup mocks to return different results on subsequent calls
        _kubernetes.WebSocketNamespacedPodExecAsync(
            podName,
            podNamespace,
            Arg.Any<IEnumerable<string>>(),
            "qdrant",
            cancellationToken: Arg.Any<CancellationToken>()
        ).Returns(deleteWebSocket, verifyWebSocket);

        // Act
        var result = await _executor.DeleteAndVerifyAsync(
            podName, podNamespace, fullPath, isDirectory, description, CancellationToken.None);

        // Assert
        Assert.That(result, Is.False);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a mock WebSocket that returns the specified output
    /// </summary>
    private static WebSocket CreateMockWebSocket(string output)
    {
        var mockWebSocket = Substitute.For<WebSocket>();
        var outputBytes = Encoding.UTF8.GetBytes(output);
        var callCount = 0;

        mockWebSocket.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var buffer = callInfo.ArgAt<ArraySegment<byte>>(0);
                
                if (callCount == 0)
                {
                    // First call - return the data
                    var bytesToCopy = Math.Min(outputBytes.Length, buffer.Count);
                    Array.Copy(outputBytes, 0, buffer.Array!, buffer.Offset, bytesToCopy);
                    callCount++;
                    return new WebSocketReceiveResult(
                        bytesToCopy,
                        WebSocketMessageType.Text,
                        outputBytes.Length <= buffer.Count); // endOfMessage
                }
                else
                {
                    // Second call - signal close
                    return new WebSocketReceiveResult(
                        0,
                        WebSocketMessageType.Close,
                        true,
                        WebSocketCloseStatus.NormalClosure,
                        "Closed");
                }
            });

        mockWebSocket.State.Returns(WebSocketState.Open, WebSocketState.Closed);
        mockWebSocket.CloseStatus.Returns(null, WebSocketCloseStatus.NormalClosure);

        return mockWebSocket;
    }

    #endregion
}

