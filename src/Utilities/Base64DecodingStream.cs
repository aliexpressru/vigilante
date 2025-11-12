namespace Vigilante.Utilities;

/// <summary>
/// Stream wrapper that decodes base64 data on the fly
/// </summary>
internal class Base64DecodingStream : Stream
{
    private readonly Stream _innerStream;
    private readonly byte[] _base64Buffer = new byte[4096]; // Buffer for base64 text
    private readonly Queue<byte> _decodedQueue = new Queue<byte>(); // Queue for decoded bytes
    private bool _endOfStream;

    public Base64DecodingStream(Stream innerStream)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var totalCopied = 0;

        // First, return any decoded bytes from queue
        while (_decodedQueue.Count > 0 && totalCopied < count)
        {
            buffer[offset + totalCopied] = _decodedQueue.Dequeue();
            totalCopied++;
        }

        if (totalCopied >= count)
            return totalCopied;

        // If we need more data and haven't reached end of stream, read and decode more
        while (!_endOfStream && totalCopied < count)
        {
            var bytesRead = await _innerStream.ReadAsync(_base64Buffer, 0, _base64Buffer.Length, cancellationToken);
            
            if (bytesRead == 0)
            {
                _endOfStream = true;
                break;
            }

            // Decode base64 chunk
            try
            {
                var base64Text = System.Text.Encoding.ASCII.GetString(_base64Buffer, 0, bytesRead);
                
                // Remove any whitespace (newlines, spaces, etc.)
                base64Text = new string(base64Text.Where(c => !char.IsWhiteSpace(c)).ToArray());
                
                if (string.IsNullOrEmpty(base64Text))
                    continue;

                // Decode base64
                var decoded = Convert.FromBase64String(base64Text);
                
                // Add to queue
                foreach (var b in decoded)
                {
                    _decodedQueue.Enqueue(b);
                }

                // Copy to output buffer
                while (_decodedQueue.Count > 0 && totalCopied < count)
                {
                    buffer[offset + totalCopied] = _decodedQueue.Dequeue();
                    totalCopied++;
                }
            }
            catch (FormatException)
            {
                // Invalid base64, skip this chunk
                continue;
            }
        }

        return totalCopied;
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

