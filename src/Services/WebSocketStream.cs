using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using Vigilante.Extensions;

namespace Vigilante.Services;

/// <summary>
/// Stream wrapper for WebSocket that reads binary data from a pod
/// </summary>
internal sealed class WebSocketStream : Stream
{
    private readonly WebSocket _webSocket;
    private readonly ILogger _logger;
    private readonly string _filePath;
    private readonly string _podName;
    private readonly long? _expectedSize;
    private bool _disposed;
    private long _totalBytesRead;
    private byte[] _leftoverBuffer = [];
    private int _leftoverOffset;
    private int _leftoverCount;
    private int _stdoutMessages;
    private int _stderrMessages;
    private int _otherMessages;
    private long _totalWebSocketBytes;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _totalBytesRead;
        set => throw new NotSupportedException();
    }

    public WebSocketStream(WebSocket webSocket, ILogger logger, string filePath, string podName, long? expectedSize = null)
    {
        _webSocket = webSocket;
        _logger = logger;
        _filePath = filePath;
        _podName = podName;
        _expectedSize = expectedSize;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_leftoverCount > 0)
        {
            var bytesToCopy = Math.Min(_leftoverCount, buffer.Length);
            _leftoverBuffer.AsMemory(_leftoverOffset, bytesToCopy).CopyTo(buffer);
            _leftoverOffset += bytesToCopy;
            _leftoverCount -= bytesToCopy;
            _totalBytesRead += bytesToCopy;
            return bytesToCopy;
        }

        while (_webSocket.State == WebSocketState.Open)
        {
            try
            {
                using var tempMemoryOwner = MemoryPool<byte>.Shared.Rent(65536);
                var tempMemory = tempMemoryOwner.Memory[..65536];

                var result = await _webSocket.ReceiveAsync(tempMemory, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation(
                        "WebSocket closed for {FilePath} from pod {PodName}. Total bytes read: {TotalBytes}",
                        _filePath,
                        _podName,
                        _totalBytesRead
                    );
                    return 0;
                }

                _totalWebSocketBytes += result.Count;

                if (result.Count == 0)
                {
                    continue;
                }

                // First byte is the channel
                var channel = tempMemory.Span[0];

                if (channel == 1)
                {
                    _stdoutMessages++;
                }
                else if (channel == 2)
                {
                    _stderrMessages++;
                }
                else
                {
                    _otherMessages++;
                }

                // Skip messages from non-stdout channels (like stderr)
                if (channel != 1)
                {
                    // This is stderr or another channel, log it and skip
                    if (channel == 2 && result.Count > 1)
                    {
                        var stderrMessage = Encoding.UTF8.GetString(tempMemory[1..result.Count].Span);
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

                var dataLength = result.Count - 1;
                var bytesToCopy = Math.Min(dataLength, buffer.Length);

                tempMemory.Slice(1, bytesToCopy).CopyTo(buffer);
                _totalBytesRead += bytesToCopy;

                if (dataLength > bytesToCopy)
                {
                    var leftoverSize = dataLength - bytesToCopy;
                    _leftoverBuffer = GC.AllocateUninitializedArray<byte>(leftoverSize);
                    tempMemory.Slice(1 + bytesToCopy, leftoverSize).CopyTo(_leftoverBuffer);
                    _leftoverOffset = 0;
                    _leftoverCount = leftoverSize;
                }

                return bytesToCopy;
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error reading from WebSocket for {FilePath} from pod {PodName}. Total bytes read: {TotalBytes}",
                    _filePath,
                    _podName,
                    _totalBytesRead
                );
                return 0;
            }
        }

        return 0;
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
            var tempBuffer = ArrayPool<byte>.Shared.Rent(65536); // 64KB buffer for WebSocket messages

            try
            {
                // Kubernetes WebSocket uses a channel prefix (first byte):
                // 0 = stdin, 1 = stdout, 2 = stderr, 3 = error/resize
                // Read into a large buffer to handle complete WebSocket message

                var segment = new ArraySegment<byte>(tempBuffer, 0, 65536);
                var result = await _webSocket.ReceiveAsync(segment, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation(
                        "WebSocket closed for {FilePath} from pod {PodName}. Total bytes read: {TotalBytes}",
                        _filePath,
                        _podName,
                        _totalBytesRead
                    );
                    return 0; // End of stream
                }

                _totalWebSocketBytes += result.Count;

                if (result.Count == 0)
                {
                    continue;
                }

                // First byte is the channel
                var channel = tempBuffer[0];

                // Track message types
                if (channel == 1)
                {
                    _stdoutMessages++;
                }
                else if (channel == 2)
                {
                    _stderrMessages++;
                }
                else
                {
                    _otherMessages++;
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
                }

                return bytesToCopy;
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error reading from WebSocket for {FilePath} from pod {PodName}. Total bytes read: {TotalBytes}",
                    _filePath,
                    _podName,
                    _totalBytesRead
                );
                return 0;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(tempBuffer);
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
                        """
                        {Status} Download completed: {FilePath} from {PodName}
                            Expected file size: {ExpectedSize} bytes ({ExpectedSizeFormatted})
                            Data bytes (stdout only): {DataBytes} bytes ({FormattedDataSize})
                            Total WebSocket bytes: {TotalWSBytes} bytes ({FormattedWSSize})
                            Channel overhead: {Overhead} bytes ({OverheadPercent:F2}%)
                            {ExtraStatus} Extra data read: {ExtraBytes} bytes ({ExtraSizeFormatted})
                            Messages: stdout={StdoutCount}, stderr={StderrCount}, other={OtherCount}
                        """,
                        status,
                        _filePath,
                        _podName,
                        _expectedSize.Value,
                        _expectedSize.Value.ToPrettySize(),
                        _totalBytesRead,
                        _totalBytesRead.ToPrettySize(),
                        _totalWebSocketBytes,
                        _totalWebSocketBytes.ToPrettySize(),
                        _totalWebSocketBytes - _totalBytesRead,
                        (_totalWebSocketBytes - _totalBytesRead) * 100.0 / Math.Max(_totalWebSocketBytes, 1),
                        extraBytes >= 0 ? "ERROR" : "OK",
                        extraBytes,
                        Math.Abs(extraBytes).ToPrettySize(),
                        _stdoutMessages,
                        _stderrMessages,
                        _otherMessages
                    );
                }
                else
                {
                    _logger.LogInformation(
                        """
                        Download completed: {FilePath} from {PodName}
                            Data bytes (stdout only): {DataBytes} bytes ({FormattedDataSize})
                            Total WebSocket bytes: {TotalWSBytes} bytes ({FormattedWSSize})
                            Channel overhead: {Overhead} bytes ({OverheadPercent:F2}%)
                            Messages: stdout={StdoutCount}, stderr={StderrCount}, other={OtherCount}
                        """,
                        _filePath,
                        _podName,
                        _totalBytesRead,
                        _totalBytesRead.ToPrettySize(),
                        _totalWebSocketBytes,
                        _totalWebSocketBytes.ToPrettySize(),
                        _totalWebSocketBytes - _totalBytesRead,
                        (_totalWebSocketBytes - _totalBytesRead) * 100.0 / Math.Max(_totalWebSocketBytes, 1),
                        _stdoutMessages,
                        _stderrMessages,
                        _otherMessages
                    );
                }
                _webSocket.Dispose();
            }
            _disposed = true;
        }

        base.Dispose(disposing);
    }
}
