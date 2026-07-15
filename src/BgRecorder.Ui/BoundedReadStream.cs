namespace BgRecorder.Ui;

/// <summary>
/// Seekable window over a larger stream. WebView2 receives this for a 206 response so it cannot
/// read past the advertised byte range even when the underlying file is several gigabytes long.
/// </summary>
public sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private readonly bool _leaveOpen;
    private readonly Action<BoundedReadStream>? _onReleased;
    private long _position;
    private bool _disposed;

    /// <param name="onReleased">
    /// Invoked exactly once when this stream disposes (whether via EOF/error self-release or an
    /// explicit Dispose). The host uses it to drop the stream from its outstanding-request registry
    /// so a stream that WebView2 abandons on a seek is still released when the window closes.
    /// </param>
    public BoundedReadStream(
        Stream inner,
        long offset,
        long length,
        bool leaveOpen = false,
        Action<BoundedReadStream>? onReleased = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (!inner.CanRead || !inner.CanSeek)
        {
            throw new ArgumentException("The inner stream must be readable and seekable.", nameof(inner));
        }

        if (offset > inner.Length || length > inner.Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The requested window exceeds the inner stream.");
        }

        _inner = inner;
        _start = offset;
        _length = length;
        _leaveOpen = leaveOpen;
        _onReleased = onReleased;
        _inner.Position = offset;
    }

    public override bool CanRead => !_disposed && _inner.CanRead;
    public override bool CanSeek => !_disposed && _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length { get { ThrowIfDisposed(); return _length; } }

    public override long Position
    {
        get { ThrowIfDisposed(); return _position; }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        ThrowIfDisposed();
        var capped = (int)Math.Min(count, _length - _position);
        if (capped == 0)
        {
            if (_position >= _length)
            {
                ReleaseAfterCompletion();
            }
            return 0;
        }

        try
        {
            EnsureInnerPosition();
            var read = _inner.Read(buffer, offset, capped);
            _position += read;
            if (read == 0)
            {
                ReleaseAfterCompletion();
            }
            return read;
        }
        catch
        {
            ReleaseAfterCompletion();
            throw;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        var capped = (int)Math.Min(buffer.Length, _length - _position);
        if (capped == 0)
        {
            if (_position >= _length)
            {
                ReleaseAfterCompletion();
            }
            return 0;
        }

        try
        {
            EnsureInnerPosition();
            var read = _inner.Read(buffer[..capped]);
            _position += read;
            if (read == 0)
            {
                ReleaseAfterCompletion();
            }
            return read;
        }
        catch
        {
            ReleaseAfterCompletion();
            throw;
        }
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var capped = (int)Math.Min(buffer.Length, _length - _position);
        if (capped == 0)
        {
            if (_position >= _length)
            {
                ReleaseAfterCompletion();
            }
            return 0;
        }

        try
        {
            EnsureInnerPosition();
            var read = await _inner.ReadAsync(buffer[..capped], cancellationToken).ConfigureAwait(false);
            _position += read;
            if (read == 0)
            {
                ReleaseAfterCompletion();
            }
            return read;
        }
        catch
        {
            ReleaseAfterCompletion();
            throw;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        long target;
        try
        {
            target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
        }
        catch (OverflowException)
        {
            throw new IOException("Seek would move outside the bounded stream.");
        }

        if (target < 0 || target > _length)
        {
            throw new IOException("Seek would move outside the bounded stream.");
        }

        _inner.Seek(checked(_start + target), SeekOrigin.Begin);
        _position = target;
        return _position;
    }

    public override void Flush() => ThrowIfDisposed();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                if (!_leaveOpen)
                {
                    _inner.Dispose();
                }

                _onReleased?.Invoke(this);
            }
        }

        base.Dispose(disposing);
    }

    private void EnsureInnerPosition()
    {
        var expected = checked(_start + _position);
        if (_inner.Position != expected)
        {
            _inner.Position = expected;
        }
    }

    /// <summary>
    /// WebView2 does not dispose custom response streams. Release ownership when it observes EOF or
    /// a read failure; suppress disposal errors here so they never replace the actual read result.
    /// </summary>
    private void ReleaseAfterCompletion()
    {
        try
        {
            Dispose();
        }
        catch
        {
            // Best effort on the transport completion path.
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
