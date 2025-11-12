using k8s;
using System.Net.WebSockets;
using System.Text;
using Vigilante.Extensions;
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

    // Command: cat {path} 2>/dev/null || echo ''
    // - "cat {path}": Read file contents
    // - "2>/dev/null": Redirect errors to null (suppress error messages)
    // - "||": Execute next command if previous fails
    // - "echo ''": Return empty string if file not found (prevents error)
    private const string GetFileContentCommand = "cat {0} 2>/dev/null || echo ''";

    // Command: base64 -w 0 {path}
    // - "base64": Encode file to base64 (ensures data integrity over WebSocket)
    // - "-w 0": No line wrapping (single line output)
    // - "{path}": Path to file to encode
    private const string StreamFileCommand = "base64 -w 0 {0}";

    // Command: stat -c %s {path}
    // - "stat": Display file status
    // - "-c %s": Format string to print only file size in bytes
    // - "{path}": Path to the file
    private const string GetFileSizeCommand = "stat -c %s {0}";

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
                // Kubernetes WebSocket uses channel prefix (first byte):
                // 0 = stdin, 1 = stdout, 2 = stderr, 3 = error/resize
                // Skip the first byte (channel) and only read actual data
                var dataStart = 1;
                var dataLength = result.Count - 1;
                
                if (dataLength > 0)
                {
                    output.Append(Encoding.UTF8.GetString(buffer, dataStart, dataLength));
                }
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
    /// Gets exact file size in bytes using stat command
    /// </summary>
    public async Task<long?> GetFileSizeInBytesAsync(
        string podName,
        string podNamespace,
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Getting file size for {FilePath} on pod {PodName} in namespace {Namespace}", 
                filePath, podName, podNamespace);
            
            var command = string.Format(GetFileSizeCommand, filePath);
            _logger.LogInformation("Executing command: stat -c %s {FilePath}", filePath);
            
            var output = await ExecuteCommandAsync(podName, podNamespace, command, cancellationToken);
            
            _logger.LogInformation("stat command raw output: '{Output}' (length: {Length})", 
                output, output?.Length ?? 0);
            
            if (string.IsNullOrWhiteSpace(output))
            {
                _logger.LogWarning("stat command returned empty output for {FilePath}", filePath);
                return null;
            }
            
            // Aggressively trim all whitespace including newlines, tabs, spaces
            var trimmedOutput = output.Trim().Trim('\n', '\r', '\t', ' ');
            
            _logger.LogInformation("After trim: '{TrimmedOutput}' (length: {Length})", 
                trimmedOutput, trimmedOutput.Length);
            
            if (long.TryParse(trimmedOutput, out var size))
            {
                _logger.LogInformation("✅ Got file size for {FilePath}: {Size} bytes", filePath, size);
                return size;
            }
            
            // Log each character's ASCII code for debugging
            var charCodes = string.Join(", ", trimmedOutput.Select((c, i) => $"[{i}]='{c}'({(int)c})"));
            _logger.LogWarning("❌ Could not parse file size. Output: '{Output}', Chars: {CharCodes}", 
                trimmedOutput, charCodes);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to get file size for {FilePath} on pod {PodName}", filePath, podName);
            return null;
        }
    }

    /// <summary>
    /// Gets content of a file from pod
    /// </summary>
    public async Task<string?> GetFileContentAsync(
        string podName,
        string podNamespace,
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Reading file content from {FilePath} on pod {PodName}", filePath, podName);
            
            var command = string.Format(GetFileContentCommand, filePath);
            var content = await ExecuteCommandAsync(podName, podNamespace, command, cancellationToken);
            
            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogDebug("File not found or empty at {FilePath}", filePath);
                return null;
            }
            
            return content.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read file content from {FilePath}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Downloads a file from a pod as a stream using cat command
    /// </summary>
    public Task<Stream?> DownloadFileAsync(
        string podName,
        string podNamespace,
        string filePath,
        CancellationToken cancellationToken)
    {
        return DownloadFileAsync(podName, podNamespace, filePath, null, cancellationToken);
    }

    /// <summary>
    /// Downloads a file from a pod as a stream using cat command with optional expected size for logging
    /// </summary>
    public async Task<Stream?> DownloadFileAsync(
        string podName,
        string podNamespace,
        string filePath,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Downloading file {FilePath} from pod {PodName} in namespace {Namespace}",
                filePath, podName, podNamespace);

            // Use cat to stream file contents directly
            var command = string.Format(StreamFileCommand, filePath);
            
            var webSocket = await _kubernetes.WebSocketNamespacedPodExecAsync(
                podName,
                podNamespace,
                new[] { "sh", "-c", command },
                "qdrant",
                cancellationToken: cancellationToken);

            // Create a stream that will read from WebSocket
            var stream = new WebSocketStream(webSocket, _logger, filePath, podName, expectedSize);
            
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
        private readonly long? _expectedSize;
        private bool _disposed;
        private long _totalBytesRead;
        private byte[] _leftoverBuffer = Array.Empty<byte>();
        private int _leftoverOffset;
        private int _leftoverCount;
        private int _stdoutMessages;
        private int _stderrMessages;
        private int _otherMessages;
        private long _totalWebSocketBytes;

        public WebSocketStream(WebSocket webSocket, ILogger logger, string filePath, string podName, long? expectedSize = null)
        {
            _webSocket = webSocket;
            _logger = logger;
            _filePath = filePath;
            _podName = podName;
            _expectedSize = expectedSize;
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
            // First, return any leftover data from previous read
            if (_leftoverCount > 0)
            {
                var bytesToCopy = Math.Min(_leftoverCount, count);
                Array.Copy(_leftoverBuffer, _leftoverOffset, buffer, offset, bytesToCopy);
                _leftoverOffset += bytesToCopy;
                _leftoverCount -= bytesToCopy;
                _totalBytesRead += bytesToCopy;
                return bytesToCopy;
            }

            while (_webSocket.State == WebSocketState.Open)
            {
                try
                {
                    // Kubernetes WebSocket uses a channel prefix (first byte):
                    // 0 = stdin, 1 = stdout, 2 = stderr, 3 = error/resize
                    // Read into a large buffer to handle complete WebSocket message
                    var tempBuffer = new byte[65536]; // 64KB buffer for WebSocket messages
                    var segment = new ArraySegment<byte>(tempBuffer);
                    var result = await _webSocket.ReceiveAsync(segment, cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("WebSocket closed for {FilePath} from pod {PodName}. Total bytes read: {TotalBytes}",
                            _filePath, _podName, _totalBytesRead);
                        return 0; // End of stream
                    }

                    _totalWebSocketBytes += result.Count;

                    if (result.Count == 0)
                    {
                        // Empty message, continue reading
                        _logger.LogDebug("Received empty WebSocket message, continuing...");
                        continue;
                    }

                    // First byte is the channel
                    var channel = tempBuffer[0];
                    
                    // Log VERY FIRST WebSocket message BEFORE any counting
                    if (_stdoutMessages == 0 && _stderrMessages == 0 && _otherMessages == 0)
                    {
                        var first20Bytes = string.Join(" ", tempBuffer.Take(Math.Min(21, result.Count)).Select(b => b.ToString("X2")));
                        _logger.LogInformation("🔵 VERY FIRST WebSocket message (before processing): Channel={Channel}, Count={Count}, First20BytesWithChannel=[{Bytes}]",
                            channel, result.Count, first20Bytes);
                    }
                    
                    // Track message types
                    if (channel == 1)
                        _stdoutMessages++;
                    else if (channel == 2)
                        _stderrMessages++;
                    else
                        _otherMessages++;
                    
                    // Log received message details for debugging
                    if (_totalBytesRead < 1024 * 1024) // Log details only for first MB
                    {
                        _logger.LogDebug("Received WebSocket message: Channel={Channel}, Count={Count}, MessageType={MessageType}, EndOfMessage={EndOfMessage}",
                            channel, result.Count, result.MessageType, result.EndOfMessage);
                    }
                    
                    // Skip messages from non-stdout channels (like stderr)
                    if (channel != 1)
                    {
                        // This is stderr or another channel, log it and skip
                        if (channel == 2 && result.Count > 1)
                        {
                            var stderrMessage = Encoding.UTF8.GetString(tempBuffer, 1, result.Count - 1);
                            _logger.LogWarning("stderr from {PodName}: {Message}", _podName, stderrMessage.Trim());
                        }
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
                    
                    // Log first stdout message for debugging data corruption
                    if (_stdoutMessages == 1)
                    {
                        var first20Bytes = string.Join(" ", tempBuffer.Skip(1).Take(Math.Min(20, dataLength)).Select(b => b.ToString("X2")));
                        _logger.LogInformation("📦 FIRST stdout message #1: Channel={Channel}, Count={Count}, DataLength={DataLength}, First20Bytes=[{Bytes}]",
                            channel, result.Count, dataLength, first20Bytes);
                    }
                    
                    // Also log second message to see pattern
                    if (_stdoutMessages == 2)
                    {
                        var first10Bytes = string.Join(" ", tempBuffer.Skip(1).Take(Math.Min(10, dataLength)).Select(b => b.ToString("X2")));
                        _logger.LogInformation("📦 SECOND stdout message #2: Count={Count}, DataLength={DataLength}, First10Bytes=[{Bytes}]",
                            result.Count, dataLength, first10Bytes);
                    }
                    
                    // Copy data (excluding channel byte) to output buffer
                    Array.Copy(tempBuffer, 1, buffer, offset, bytesToCopy);
                    
                    _totalBytesRead += bytesToCopy;
                    
                    // If we have more data than requested, store it in leftover buffer
                    if (dataLength > bytesToCopy)
                    {
                        var leftoverSize = dataLength - bytesToCopy;
                        _leftoverBuffer = new byte[leftoverSize];
                        Array.Copy(tempBuffer, 1 + bytesToCopy, _leftoverBuffer, 0, leftoverSize);
                        _leftoverOffset = 0;
                        _leftoverCount = leftoverSize;
                        
                        _logger.LogDebug("Stored {LeftoverBytes} bytes in leftover buffer", leftoverSize);
                    }
                    
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
                    if (_expectedSize.HasValue)
                    {
                        var extraBytes = _totalBytesRead - _expectedSize.Value;
                        var status = extraBytes == 0 ? "✅" : "⚠️";
                        
                        _logger.LogInformation(
                            "{Status} Download completed: {FilePath} from {PodName}\n" +
                            "   📏 Expected file size: {ExpectedSize} bytes ({ExpectedSizeFormatted})\n" +
                            "   📊 Data bytes (stdout only): {DataBytes} bytes ({FormattedDataSize})\n" +
                            "   📦 Total WebSocket bytes: {TotalWSBytes} bytes ({FormattedWSSize})\n" +
                            "   🔧 Channel overhead: {Overhead} bytes ({OverheadPercent:F2}%)\n" +
                            "   {ExtraStatus} Extra data read: {ExtraBytes} bytes ({ExtraSizeFormatted})\n" +
                            "   📨 Messages: stdout={StdoutCount}, stderr={StderrCount}, other={OtherCount}",
                            status, _filePath, _podName,
                            _expectedSize.Value, _expectedSize.Value.ToPrettySize(),
                            _totalBytesRead, _totalBytesRead.ToPrettySize(),
                            _totalWebSocketBytes, _totalWebSocketBytes.ToPrettySize(),
                            _totalWebSocketBytes - _totalBytesRead, 
                            (_totalWebSocketBytes - _totalBytesRead) * 100.0 / Math.Max(_totalWebSocketBytes, 1),
                            extraBytes >= 0 ? "❌" : "✅", extraBytes, Math.Abs(extraBytes).ToPrettySize(),
                            _stdoutMessages, _stderrMessages, _otherMessages);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "✅ Download completed: {FilePath} from {PodName}\n" +
                            "   📊 Data bytes (stdout only): {DataBytes} bytes ({FormattedDataSize})\n" +
                            "   📦 Total WebSocket bytes: {TotalWSBytes} bytes ({FormattedWSSize})\n" +
                            "   🔧 Channel overhead: {Overhead} bytes ({OverheadPercent:F2}%)\n" +
                            "   📨 Messages: stdout={StdoutCount}, stderr={StderrCount}, other={OtherCount}",
                            _filePath, _podName,
                            _totalBytesRead, _totalBytesRead.ToPrettySize(),
                            _totalWebSocketBytes, _totalWebSocketBytes.ToPrettySize(),
                            _totalWebSocketBytes - _totalBytesRead, 
                            (_totalWebSocketBytes - _totalBytesRead) * 100.0 / Math.Max(_totalWebSocketBytes, 1),
                            _stdoutMessages, _stderrMessages, _otherMessages);
                    }
                    _webSocket.Dispose();
                }
                _disposed = true;
            }
            base.Dispose(disposing);
        }
    }
}

