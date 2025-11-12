namespace Vigilante.Utilities;

/// <summary>
/// Stream wrapper that limits reading to a specific number of bytes
/// </summary>
internal class LimitedStream : Stream
{
    private readonly Stream _innerStream;
    private readonly long _maxBytes;
    private long _bytesRead;

    public LimitedStream(Stream innerStream, long maxBytes)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _maxBytes = maxBytes;
        _bytesRead = 0;
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _maxBytes;
    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_bytesRead >= _maxBytes)
            return 0; // EOF reached

        var bytesToRead = (int)Math.Min(count, _maxBytes - _bytesRead);
        var bytesActuallyRead = _innerStream.Read(buffer, offset, bytesToRead);
        _bytesRead += bytesActuallyRead;
        
        return bytesActuallyRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_bytesRead >= _maxBytes)
            return 0; // EOF reached

        var bytesToRead = (int)Math.Min(count, _maxBytes - _bytesRead);
        var bytesActuallyRead = await _innerStream.ReadAsync(buffer, offset, bytesToRead, cancellationToken);
        _bytesRead += bytesActuallyRead;
        
        return bytesActuallyRead;
    }

    public override void Flush() => _innerStream.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _innerStream?.Dispose();
        }
        base.Dispose(disposing);
    }
}

