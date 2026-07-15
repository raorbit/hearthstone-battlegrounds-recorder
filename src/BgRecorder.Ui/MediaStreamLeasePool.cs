using System.Collections.Concurrent;

namespace BgRecorder.Ui;

/// <summary>
/// Owns the file handles behind WebView2 media responses. WebView2 does not dispose a custom
/// response stream when it abandons a request, so idle leases park their underlying handle while
/// keeping the response stream itself resumable. A later read transparently reopens the resource at
/// the lease's logical position. The handle cap is immediate for idle leases; a transient overage is
/// allowed only while every candidate is inside an active read, and completion reapplies the cap.
/// </summary>
public sealed class MediaStreamLeasePool : IDisposable
{
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<ReopenableBoundedStream, Activity> _openStreams = new();
    private readonly int _maxOpenStreams;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _timer;
    private int _disposed;

    public MediaStreamLeasePool(
        int maxOpenStreams = 8,
        TimeSpan? idleTimeout = null,
        TimeSpan? sweepInterval = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxOpenStreams, 1);

        _maxOpenStreams = maxOpenStreams;
        _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
        var interval = sweepInterval ?? DefaultSweepInterval;
        if (_idleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleTimeout), "The idle timeout must be positive.");
        }
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sweepInterval), "The sweep interval must be positive.");
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _timer = _timeProvider.CreateTimer(
            static state => ((MediaStreamLeasePool)state!).Sweep(),
            this,
            interval,
            interval);
    }

    /// <summary>The number of leases that currently hold an open underlying stream.</summary>
    public int OpenStreamCount => _openStreams.Count;

    /// <summary>
    /// Transfers ownership of <paramref name="initialStream"/> into a resumable bounded lease.
    /// <paramref name="reopenStream"/> must open the same immutable resource when a parked lease is
    /// read again.
    /// </summary>
    public Stream Lease(
        Stream initialStream,
        Func<Stream> reopenStream,
        long offset,
        long length)
    {
        ArgumentNullException.ThrowIfNull(initialStream);
        ArgumentNullException.ThrowIfNull(reopenStream);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var lease = new ReopenableBoundedStream(
            this,
            initialStream,
            reopenStream,
            offset,
            length);
        if (!TryRegisterOpen(lease))
        {
            lease.Dispose();
            throw new ObjectDisposedException(nameof(MediaStreamLeasePool));
        }

        TrimToLimit();
        return lease;
    }

    /// <summary>
    /// Parks expired idle handles and reapplies the open-handle limit. Exposed so hosts can force a
    /// sweep during lifecycle transitions and tests can advance a deterministic clock.
    /// </summary>
    public void Sweep()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var entry in _openStreams)
        {
            var lastActivity = new DateTimeOffset(
                Interlocked.Read(ref entry.Value.LastActivityUtcTicks),
                TimeSpan.Zero);
            if (now - lastActivity >= _idleTimeout)
            {
                entry.Key.TryParkIfIdle();
            }
        }

        TrimToLimit();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _timer.Dispose();
        foreach (var lease in _openStreams.Keys)
        {
            // A read already in flight is allowed to complete; the lease closes immediately after
            // that operation rather than disposing its FileStream from underneath WebView2.
            lease.Dispose();
        }

        _openStreams.Clear();
    }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal bool TryRegisterOpen(ReopenableBoundedStream lease)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        _openStreams[lease] = new Activity(_timeProvider.GetUtcNow().UtcTicks);
        if (Volatile.Read(ref _disposed) == 0)
        {
            return true;
        }

        _openStreams.TryRemove(lease, out _);
        return false;
    }

    internal void Touch(ReopenableBoundedStream lease)
    {
        if (_openStreams.TryGetValue(lease, out var activity))
        {
            Interlocked.Exchange(ref activity.LastActivityUtcTicks, _timeProvider.GetUtcNow().UtcTicks);
        }
    }

    internal void MarkClosed(ReopenableBoundedStream lease)
        => _openStreams.TryRemove(lease, out _);

    internal void OperationStarted()
        => TrimToLimit();

    internal void OperationCompleted()
        => TrimToLimit();

    private void TrimToLimit()
    {
        if (Volatile.Read(ref _disposed) != 0 || _openStreams.Count <= _maxOpenStreams)
        {
            return;
        }

        // ToArray() takes an atomic snapshot under the dictionary's locks. Ordering _openStreams
        // directly would route LINQ through ICollection.CopyTo, which reads Count, allocates, then
        // copies without a lock; a concurrent Lease/reopen adding an entry in that gap throws
        // ArgumentException — and on the sweep timer's thread that would escape and crash the process.
        foreach (var entry in _openStreams.ToArray()
                     .OrderBy(e => Interlocked.Read(ref e.Value.LastActivityUtcTicks)))
        {
            if (_openStreams.Count <= _maxOpenStreams)
            {
                break;
            }

            // Parking is lossless: if WebView2 resumes this response, the lease reopens at its
            // logical position. TryParkIfIdle refuses to close a handle with a read in flight.
            entry.Key.TryParkIfIdle();
        }
    }

    private sealed class Activity(long lastActivityUtcTicks)
    {
        public long LastActivityUtcTicks = lastActivityUtcTicks;
    }
}

/// <summary>
/// A bounded response stream whose underlying resource may be closed while idle and reopened at the
/// same logical byte position. The wrapper remains valid while parked, unlike a disposed stream.
/// </summary>
internal sealed class ReopenableBoundedStream : Stream
{
    private readonly object _gate = new();
    private readonly MediaStreamLeasePool _owner;
    private readonly Func<Stream> _reopenStream;
    private readonly long _resourceLength;
    private readonly long _start;
    private readonly long _length;
    private Stream? _inner;
    private long _position;
    private int _activeReads;
    private bool _disposeRequested;
    private bool _disposed;

    public ReopenableBoundedStream(
        MediaStreamLeasePool owner,
        Stream initialStream,
        Func<Stream> reopenStream,
        long offset,
        long length)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(initialStream);
        ArgumentNullException.ThrowIfNull(reopenStream);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (!initialStream.CanRead || !initialStream.CanSeek)
        {
            throw new ArgumentException("The stream must be readable and seekable.", nameof(initialStream));
        }

        var resourceLength = initialStream.Length;
        if (offset > resourceLength || length > resourceLength - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The requested window exceeds the resource.");
        }

        _owner = owner;
        _inner = initialStream;
        _reopenStream = reopenStream;
        _resourceLength = resourceLength;
        _start = offset;
        _length = length;
        initialStream.Position = offset;
    }

    public override bool CanRead
    {
        get
        {
            lock (_gate)
            {
                return !_disposeRequested && !_owner.IsDisposed;
            }
        }
    }

    public override bool CanSeek => CanRead;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _length;
            }
        }
    }

    public override long Position
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _position;
            }
        }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        var operation = BeginRead(count);
        if (operation is null)
        {
            return 0;
        }

        try
        {
            var read = operation.Value.Stream.Read(buffer, offset, operation.Value.Count);
            CompleteRead(read);
            return read;
        }
        catch
        {
            FailRead();
            throw;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        var operation = BeginRead(buffer.Length);
        if (operation is null)
        {
            return 0;
        }

        try
        {
            var read = operation.Value.Stream.Read(buffer[..operation.Value.Count]);
            CompleteRead(read);
            return read;
        }
        catch
        {
            FailRead();
            throw;
        }
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var operation = BeginRead(buffer.Length);
        if (operation is null)
        {
            return 0;
        }

        try
        {
            var read = await operation.Value.Stream
                .ReadAsync(buffer[..operation.Value.Count], cancellationToken)
                .ConfigureAwait(false);
            CompleteRead(read);
            return read;
        }
        catch
        {
            FailRead();
            throw;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_gate)
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

            _position = target;
            if (_inner is not null)
            {
                _inner.Position = checked(_start + target);
                _owner.Touch(this);
            }
            return target;
        }
    }

    public override void Flush()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
        }
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Stream? streamToClose = null;
        if (disposing)
        {
            lock (_gate)
            {
                if (!_disposeRequested)
                {
                    _disposeRequested = true;
                    if (_activeReads == 0)
                    {
                        _disposed = true;
                        streamToClose = _inner;
                        _inner = null;
                    }
                }
            }

            if (streamToClose is not null)
            {
                CloseInner(streamToClose);
            }
        }

        base.Dispose(disposing);
    }

    internal bool TryParkIfIdle()
    {
        lock (_gate)
        {
            if (_disposeRequested || _activeReads != 0 || _inner is null)
            {
                return false;
            }

            var streamToClose = _inner;
            _inner = null;
            // Keep the gate until the old handle has left the pool. Otherwise a resumed read could
            // register its replacement between the detach and MarkClosed, and the old close would
            // accidentally remove the replacement's registry entry.
            CloseInner(streamToClose);
            return true;
        }
    }

    private (Stream Stream, int Count)? BeginRead(int requestedCount)
    {
        (Stream Stream, int Count) operation;
        lock (_gate)
        {
            ThrowIfDisposed();
            var capped = (int)Math.Min(requestedCount, _length - _position);
            if (capped == 0)
            {
                if (_position >= _length && _inner is not null)
                {
                    // Logical EOF: free the handle promptly, but leave the lease undisposed so it still
                    // honors the Stream contract. A further read returns 0, Position/Length/Seek stay
                    // usable, and a seek back re-reads by reopening the resource.
                    var completedStream = _inner;
                    _inner = null;
                    CloseInner(completedStream);
                }
                return null;
            }

            EnsureOpen();
            _inner!.Position = checked(_start + _position);
            _activeReads++;
            _owner.Touch(this);
            operation = (_inner, capped);
        }

        // A parked lease may have reopened above and temporarily pushed the pool over its cap.
        // Reapply pressure only after this lease is marked active, and without holding its gate, so
        // another idle lease can park without a cross-lease lock cycle.
        _owner.OperationStarted();
        return operation;
    }

    private void CompleteRead(int read)
    {
        Stream? streamToClose = null;
        lock (_gate)
        {
            _position += read;
            _owner.Touch(this);
            _activeReads--;
            if (read == 0)
            {
                _disposeRequested = true;
            }
            if (_disposeRequested && _activeReads == 0 && !_disposed)
            {
                _disposed = true;
                streamToClose = _inner;
                _inner = null;
            }
        }

        if (streamToClose is not null)
        {
            CloseInner(streamToClose);
        }
        _owner.OperationCompleted();
    }

    private void FailRead()
    {
        Stream? streamToClose = null;
        lock (_gate)
        {
            _activeReads--;
            _disposeRequested = true;
            if (_activeReads == 0 && !_disposed)
            {
                _disposed = true;
                streamToClose = _inner;
                _inner = null;
            }
        }

        if (streamToClose is not null)
        {
            CloseInner(streamToClose);
        }
        _owner.OperationCompleted();
    }

    private void EnsureOpen()
    {
        if (_inner is not null)
        {
            return;
        }
        if (_owner.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(MediaStreamLeasePool));
        }

        var reopened = _reopenStream();
        try
        {
            if (!reopened.CanRead || !reopened.CanSeek || reopened.Length != _resourceLength)
            {
                throw new IOException("The media resource changed while its response stream was parked.");
            }

            reopened.Position = checked(_start + _position);
            _inner = reopened;
            if (!_owner.TryRegisterOpen(this))
            {
                _inner = null;
                throw new ObjectDisposedException(nameof(MediaStreamLeasePool));
            }
        }
        catch
        {
            if (ReferenceEquals(_inner, reopened))
            {
                _inner = null;
            }
            reopened.Dispose();
            throw;
        }
    }

    private void CloseInner(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch
        {
            // Closing is best-effort on WebView2's transport path. A disposal failure must not
            // replace a successful read or escape a timer callback and terminate the process.
        }
        finally
        {
            _owner.MarkClosed(this);
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposeRequested || _owner.IsDisposed, this);
}
