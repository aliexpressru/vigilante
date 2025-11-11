using k8s;
using System.Net.WebSockets;
using System.Text;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

/// <summary>
/// Executes shell commands in Kubernetes pods via WebSocket
/// </summary>
public class PodCommandExecutor : IPodCommandExecutor
{
    private readonly IKubernetes _kubernetes;
    private readonly ILogger<PodCommandExecutor> _logger;

    // Shell command templates with detailed explanations
    
    // Command: cd {directory} && ls -1d */
    // - "cd {directory}": Change to target directory
    // - "&&": Execute next command only if cd succeeds
    // - "ls": List directory contents
    // - "-1": One entry per line (easier parsing)
    // - "-d": List directories themselves, not their contents
    // - "*/": Match only directories (trailing slash)
    private const string ListDirectoriesCommand = "cd {0} && ls -1d */";

    // Command: cd {directory} && ls -1 {pattern} 2>/dev/null || echo ''
    // - "cd {directory}": Change to target directory
    // - "&&": Execute next command only if cd succeeds
    // - "ls -1 {pattern}": List files matching pattern, one per line
    // - "2>/dev/null": Redirect errors to null (suppress error messages)
    // - "||": Execute next command if previous fails
    // - "echo ''": Return empty string if no files found (prevents error)
    private const string ListFilesCommand = "cd {0} && ls -1 {1} 2>/dev/null || echo ''";

    // Command: cd {directory} && du -sb "{item}" | cut -f1
    // - "cd {directory}": Change to target directory
    // - "&&": Execute next command only if cd succeeds
    // - "du": Disk usage command
    // - "-s": Summary only (don't show subdirectories separately)
    // - "-b": Size in bytes (instead of blocks)
    // - "\"{item}\"": Item name in quotes (handles special characters)
    // - "|": Pipe output to next command
    // - "cut -f1": Extract first field (size), excluding the path
    private const string GetSizeCommand = "cd {0} && du -sb \"{1}\" | cut -f1";

    // Command: rm -rf {path}
    // - "rm": Remove files/directories
    // - "-r": Recursive (remove directories and contents)
    // - "-f": Force (don't prompt, ignore nonexistent files)
    // - "{path}": Full path to remove
    private const string RemoveDirectoryCommand = "rm -rf {0}";

    // Command: rm -f {path}
    // - "rm": Remove files
    // - "-f": Force (don't prompt, ignore nonexistent files)
    // - "{path}": Full path to file to remove
    private const string RemoveFileCommand = "rm -f {0}";

    // Command: test -d {path} && echo 'exists' || echo 'deleted'
    // - "test -d {path}": Check if directory exists
    // - "&&": Execute next if test succeeds
    // - "echo 'exists'": Print 'exists' if directory found
    // - "||": Execute next if previous fails
    // - "echo 'deleted'": Print 'deleted' if directory not found
    private const string CheckDirectoryExistsCommand = "test -d {0} && echo 'exists' || echo 'deleted'";

    // Command: test -f {path} && echo 'exists' || echo 'deleted'
    // - "test -f {path}": Check if file exists
    // - "&&": Execute next if test succeeds
    // - "echo 'exists'": Print 'exists' if file found
    // - "||": Execute next if previous fails
    // - "echo 'deleted'": Print 'deleted' if file not found
    private const string CheckFileExistsCommand = "test -f {0} && echo 'exists' || echo 'deleted'";

    public PodCommandExecutor(IKubernetes kubernetes, ILogger<PodCommandExecutor> logger)
    {
        _kubernetes = kubernetes;
        _logger = logger;
    }

    /// <summary>
    /// Lists directories in the specified path
    /// </summary>
    public async Task<List<string>> ListDirectoriesAsync(
        string podName,
        string podNamespace,
        string directory,
        CancellationToken cancellationToken)
    {
        var command = string.Format(ListDirectoriesCommand, directory);
        return await ExecuteCommandAndGetLinesAsync(podName, podNamespace, command, cancellationToken);
    }

    /// <summary>
    /// Lists files matching pattern in the specified path
    /// </summary>
    public async Task<List<string>> ListFilesAsync(
        string podName,
        string podNamespace,
        string directory,
        string pattern,
        CancellationToken cancellationToken)
    {
        var command = string.Format(ListFilesCommand, directory, pattern);
        return await ExecuteCommandAndGetLinesAsync(podName, podNamespace, command, cancellationToken);
    }

    /// <summary>
    /// Gets the size of a file or directory in bytes
    /// </summary>
    public async Task<long?> GetSizeAsync(
        string podName,
        string podNamespace,
        string baseDirectory,
        string itemName,
        CancellationToken cancellationToken)
    {
        try
        {
            var command = string.Format(GetSizeCommand, baseDirectory, itemName);
            var rawOutput = await ExecuteCommandAsync(podName, podNamespace, command, cancellationToken);
            
            var output = rawOutput
                .Trim()
                .Replace("\n", "")
                .Replace("\r", "")
                .Replace("\0", "");
            
            _logger.LogDebug("Received size data for {Item}: '{Output}'", itemName, output);

            var cleanedOutput = new string(output.Where(char.IsDigit).ToArray());
            if (!string.IsNullOrEmpty(cleanedOutput) && long.TryParse(cleanedOutput, out var sizeBytes))
            {
                return sizeBytes;
            }
            
            _logger.LogWarning("Failed to parse size for {Item}: '{Output}'", itemName, output);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get size for {Item} in pod {PodName}", itemName, podName);
            return null;
        }
    }

    /// <summary>
    /// Deletes a file or directory and verifies deletion
    /// </summary>
    public async Task<bool> DeleteAndVerifyAsync(
        string podName,
        string podNamespace,
        string fullPath,
        bool isDirectory,
        string itemDescription,
        CancellationToken cancellationToken)
    {
        try
        {
            // Execute delete command
            var deleteCommand = isDirectory 
                ? string.Format(RemoveDirectoryCommand, fullPath)
                : string.Format(RemoveFileCommand, fullPath);
            
            var deleteOutput = await ExecuteCommandAsync(podName, podNamespace, deleteCommand, cancellationToken);

            // Check for errors in output
            if (!string.IsNullOrEmpty(deleteOutput) && 
                (deleteOutput.Contains("error", StringComparison.OrdinalIgnoreCase) || 
                 deleteOutput.Contains("permission denied", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogError("Failed to delete {Description}: {Output}", itemDescription, deleteOutput);
                return false;
            }

            // Verify deletion
            var verifyCommand = isDirectory 
                ? string.Format(CheckDirectoryExistsCommand, fullPath)
                : string.Format(CheckFileExistsCommand, fullPath);
            
            var verifyOutput = await ExecuteCommandAsync(podName, podNamespace, verifyCommand, cancellationToken);
            var verifyResult = verifyOutput.Trim();

            if (verifyResult.Contains("deleted"))
            {
                _logger.LogInformation("✅ {Description} deleted successfully from disk on pod {PodName}", 
                    itemDescription, podName);
                return true;
            }
            else
            {
                _logger.LogError("{Description} still exists after deletion attempt on pod {PodName}", 
                    itemDescription, podName);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete {Description} on pod {PodName}", itemDescription, podName);
            return false;
        }
    }

    private async Task<string> ExecuteCommandAsync(
        string podName,
        string podNamespace,
        string command,
        CancellationToken cancellationToken)
    {
        using var webSocket = await _kubernetes.WebSocketNamespacedPodExecAsync(
            podName,
            podNamespace,
            new[] { "sh", "-c", command },
            "qdrant",
            cancellationToken: cancellationToken);

        var buffer = new byte[4096];
        var segment = new ArraySegment<byte>(buffer);
        var output = new StringBuilder(512);

        WebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(segment, cancellationToken);
            if (result.Count > 0)
            {
                output.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
        } while (!result.CloseStatus.HasValue && !cancellationToken.IsCancellationRequested);

        return output.ToString();
    }

    private async Task<List<string>> ExecuteCommandAndGetLinesAsync(
        string podName,
        string podNamespace,
        string command,
        CancellationToken cancellationToken)
    {
        var output = await ExecuteCommandAsync(podName, podNamespace, command, cancellationToken);

        return output
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(name => name
                .TrimEnd('/', ':')  // Remove both / and : that ls -1d */ can add
                .Trim()
                .Where(c => !char.IsControl(c))
                .ToArray())
            .Select(chars => new string(chars))
            .Where(name => !string.IsNullOrWhiteSpace(name) && !name.StartsWith("."))
            .ToList();
    }

    /// <summary>
    /// Downloads a file from a pod as a stream using cat command
    /// </summary>
    public async Task<Stream?> DownloadFileAsync(
        string podName,
        string podNamespace,
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Downloading file {FilePath} from pod {PodName} in namespace {Namespace}",
                filePath, podName, podNamespace);

            // Use cat to stream file contents directly
            var command = $"cat {filePath}";
            
            var webSocket = await _kubernetes.WebSocketNamespacedPodExecAsync(
                podName,
                podNamespace,
                new[] { "sh", "-c", command },
                "qdrant",
                cancellationToken: cancellationToken);

            // Create a stream that will read from WebSocket
            var stream = new WebSocketStream(webSocket, _logger, filePath, podName);
            
            _logger.LogInformation("✅ Started streaming file {FilePath} from pod {PodName}",
                filePath, podName);
            
            return stream;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download file {FilePath} from pod {PodName}",
                filePath, podName);
            return null;
        }
    }

    /// <summary>
    /// Stream wrapper for WebSocket that reads binary data from a pod
    /// </summary>
    private class WebSocketStream : Stream
    {
        private readonly WebSocket _webSocket;
        private readonly ILogger _logger;
        private readonly string _filePath;
        private readonly string _podName;
        private bool _disposed;
        private long _totalBytesRead;

        public WebSocketStream(WebSocket webSocket, ILogger logger, string filePath, string podName)
        {
            _webSocket = webSocket;
            _logger = logger;
            _filePath = filePath;
            _podName = podName;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _totalBytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (_webSocket.State == WebSocketState.Open)
            {
                try
                {
                    // Kubernetes WebSocket uses a channel prefix (first byte):
                    // 0 = stdin, 1 = stdout, 2 = stderr, 3 = error/resize
                    // Read into a larger buffer to handle the channel byte and full message
                    var tempBuffer = new byte[Math.Max(count + 1, 8192)];
                    var segment = new ArraySegment<byte>(tempBuffer);
                    var result = await _webSocket.ReceiveAsync(segment, cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogDebug("WebSocket closed for {FilePath} from pod {PodName}. Total bytes read: {TotalBytes}",
                            _filePath, _podName, _totalBytesRead);
                        return 0; // End of stream
                    }

                    if (result.Count == 0)
                    {
                        // Empty message, continue reading
                        continue;
                    }

                    // First byte is the channel
                    var channel = tempBuffer[0];
                    
                    // Skip messages from non-stdout channels (like stderr)
                    if (channel != 1)
                    {
                        // This is stderr or another channel, skip it
                        continue;
                    }

                    // Data starts from byte 1 (after channel byte)
                    if (result.Count < 2)
                    {
                        // Only channel byte, no actual data - continue reading
                        continue;
                    }

                    var dataLength = result.Count - 1; // Exclude channel byte
                    var bytesToCopy = Math.Min(dataLength, count);
                    
                    // Copy data (excluding channel byte) to output buffer
                    Array.Copy(tempBuffer, 1, buffer, offset, bytesToCopy);
                    
                    _totalBytesRead += bytesToCopy;
                    
                    if (_totalBytesRead % (1024 * 1024) == 0 || _totalBytesRead < 1024) // Log every 1MB or first KB
                    {
                        _logger.LogDebug("Downloaded {TotalBytes} bytes from {FilePath}", _totalBytesRead, _filePath);
                    }
                    
                    return bytesToCopy;
                }
                catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                {
                    _logger.LogDebug("WebSocket connection closed prematurely for {FilePath}. Total bytes read: {TotalBytes}",
                        _filePath, _totalBytesRead);
                    return 0;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error reading from WebSocket for {FilePath} from pod {PodName}. Total bytes read: {TotalBytes}",
                        _filePath, _podName, _totalBytesRead);
                    return 0;
                }
            }
            
            return 0; // WebSocket is not open
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _logger.LogInformation("Completed download of {FilePath} from pod {PodName}. Total bytes: {TotalBytes}",
                        _filePath, _podName, _totalBytesRead);
                    _webSocket.Dispose();
                }
                _disposed = true;
            }
            base.Dispose(disposing);
        }
    }
}

