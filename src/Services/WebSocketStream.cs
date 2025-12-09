using System.Net.WebSockets;
using System.Text;
using Vigilante.Extensions;

namespace Vigilante.Services;

/// <summary>
/// Stream wrapper for WebSocket that reads binary data from a pod
/// </summary>
internal class WebSocketStream : Stream
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
                    _logger.LogInformation("VERY FIRST WebSocket message (before processing): Channel={Channel}, Count={Count}, First20BytesWithChannel=[{Bytes}]",
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
                    _logger.LogInformation("FIRST stdout message #1: Channel={Channel}, Count={Count}, DataLength={DataLength}, First20Bytes=[{Bytes}]",
                        channel, result.Count, dataLength, first20Bytes);
                }
                
                // Also log second message to see pattern
                if (_stdoutMessages == 2)
                {
                    var first10Bytes = string.Join(" ", tempBuffer.Skip(1).Take(Math.Min(10, dataLength)).Select(b => b.ToString("X2")));
                    _logger.LogInformation("SECOND stdout message #2: Count={Count}, DataLength={DataLength}, First10Bytes=[{Bytes}]",
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
                    var status = extraBytes == 0 ? "OK" : "WARNING";
                    
                    _logger.LogInformation(
                        "{Status} Download completed: {FilePath} from {PodName}\n" +
                        "   Expected file size: {ExpectedSize} bytes ({ExpectedSizeFormatted})\n" +
                        "   Data bytes (stdout only): {DataBytes} bytes ({FormattedDataSize})\n" +
                        "   Total WebSocket bytes: {TotalWSBytes} bytes ({FormattedWSSize})\n" +
                        "   Channel overhead: {Overhead} bytes ({OverheadPercent:F2}%)\n" +
                        "   {ExtraStatus} Extra data read: {ExtraBytes} bytes ({ExtraSizeFormatted})\n" +
                        "   Messages: stdout={StdoutCount}, stderr={StderrCount}, other={OtherCount}",
                        status, _filePath, _podName,
                        _expectedSize.Value, _expectedSize.Value.ToPrettySize(),
                        _totalBytesRead, _totalBytesRead.ToPrettySize(),
                        _totalWebSocketBytes, _totalWebSocketBytes.ToPrettySize(),
                        _totalWebSocketBytes - _totalBytesRead, 
                        (_totalWebSocketBytes - _totalBytesRead) * 100.0 / Math.Max(_totalWebSocketBytes, 1),
                        extraBytes >= 0 ? "ERROR" : "OK", extraBytes, Math.Abs(extraBytes).ToPrettySize(),
                        _stdoutMessages, _stderrMessages, _otherMessages);
                }
                else
                {
                    _logger.LogInformation(
                        "Download completed: {FilePath} from {PodName}\n" +
                        "   Data bytes (stdout only): {DataBytes} bytes ({FormattedDataSize})\n" +
                        "   Total WebSocket bytes: {TotalWSBytes} bytes ({FormattedWSSize})\n" +
                        "   Channel overhead: {Overhead} bytes ({OverheadPercent:F2}%)\n" +
                        "   Messages: stdout={StdoutCount}, stderr={StderrCount}, other={OtherCount}",
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

