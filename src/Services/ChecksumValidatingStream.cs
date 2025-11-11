using System.Security.Cryptography;

namespace Vigilante.Services;

/// <summary>
/// Stream wrapper that calculates SHA256 checksum while reading and validates it at the end
/// </summary>
internal class ChecksumValidatingStream : Stream
{
    private readonly Stream _innerStream;
    private readonly string _expectedChecksum;
    private readonly string _snapshotName;
    private readonly ILogger _logger;
    private readonly SHA256 _sha256;
    private long _totalBytesRead;
    private bool _disposed;

    public ChecksumValidatingStream(
        Stream innerStream, 
        string expectedChecksum, 
        string snapshotName, 
        ILogger logger)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _expectedChecksum = (expectedChecksum ?? throw new ArgumentNullException(nameof(expectedChecksum))).ToLowerInvariant();
        _snapshotName = snapshotName;
        _logger = logger;
        _sha256 = SHA256.Create();
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _innerStream.Length;
    public override long Position
    {
        get => _totalBytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _innerStream.Read(buffer, offset, count);
        if (bytesRead > 0)
        {
            _sha256.TransformBlock(buffer, offset, bytesRead, null, 0);
            _totalBytesRead += bytesRead;
            
            // Log progress every 10MB
            if (_totalBytesRead % (10 * 1024 * 1024) == 0)
            {
                _logger.LogDebug("Downloaded {Bytes} MB of snapshot {Snapshot}", 
                    _totalBytesRead / (1024 * 1024), _snapshotName);
            }
        }
        else
        {
            // End of stream - finalize hash
            _sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            ValidateChecksum();
        }
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var bytesRead = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
        if (bytesRead > 0)
        {
            _sha256.TransformBlock(buffer, offset, bytesRead, null, 0);
            _totalBytesRead += bytesRead;
            
            // Log progress every 10MB
            if (_totalBytesRead % (10 * 1024 * 1024) == 0)
            {
                _logger.LogDebug("Downloaded {Bytes} MB of snapshot {Snapshot}", 
                    _totalBytesRead / (1024 * 1024), _snapshotName);
            }
        }
        else if (_totalBytesRead > 0)
        {
            // End of stream - finalize hash
            _sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            ValidateChecksum();
        }
        return bytesRead;
    }

    private void ValidateChecksum()
    {
        var hash = _sha256.Hash;
        if (hash == null)
        {
            _logger.LogWarning("Could not compute hash for snapshot {Snapshot}", _snapshotName);
            return;
        }

        var actualChecksum = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        
        _logger.LogInformation(
            "Snapshot {Snapshot} download completed. Size: {Size} bytes, Expected checksum: {Expected}, Actual checksum: {Actual}",
            _snapshotName, _totalBytesRead, _expectedChecksum, actualChecksum);

        if (actualChecksum != _expectedChecksum)
        {
            _logger.LogError(
                "❌ CHECKSUM MISMATCH for snapshot {Snapshot}! Expected: {Expected}, Actual: {Actual}. File may be corrupted!",
                _snapshotName, _expectedChecksum, actualChecksum);
        }
        else
        {
            _logger.LogInformation("✅ Checksum validation PASSED for snapshot {Snapshot}", _snapshotName);
        }
    }

    public override void Flush() => _innerStream.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _sha256.Dispose();
                _innerStream.Dispose();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}

