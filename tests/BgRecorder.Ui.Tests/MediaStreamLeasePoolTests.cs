using BgRecorder.Ui;
using Xunit;

namespace BgRecorder.Ui.Tests;

public sealed class MediaStreamLeasePoolTests
{
    [Fact]
    public void Idle_lease_closes_its_handle_and_resumes_from_the_same_position()
    {
        var clock = new ManualTimeProvider();
        using var pool = CreatePool(clock, maxOpenStreams: 4, idleTimeout: TimeSpan.FromSeconds(10));
        var initial = new TrackingStream(Enumerable.Range(0, 8).Select(i => (byte)i).ToArray());
        TrackingStream? reopened = null;
        using var lease = pool.Lease(
            initial,
            () => reopened = new TrackingStream(Enumerable.Range(0, 8).Select(i => (byte)i).ToArray()),
            offset: 2,
            length: 4);
        var buffer = new byte[2];

        Assert.Equal(2, lease.Read(buffer));
        Assert.Equal(new byte[] { 2, 3 }, buffer);
        Assert.Equal(1, pool.OpenStreamCount);

        clock.Advance(TimeSpan.FromSeconds(11));
        pool.Sweep();

        Assert.True(initial.WasDisposed);
        Assert.Equal(0, pool.OpenStreamCount);
        Assert.True(lease.CanRead); // parked is not disposed

        Assert.Equal(2, lease.Read(buffer));
        Assert.Equal(new byte[] { 4, 5 }, buffer);
        Assert.NotNull(reopened);
        Assert.False(reopened.WasDisposed);
        Assert.Equal(1, pool.OpenStreamCount);

        Assert.Equal(0, lease.Read(buffer));
        Assert.True(reopened.WasDisposed);
        Assert.Equal(0, pool.OpenStreamCount);
    }

    [Fact]
    public void Count_pressure_parks_the_oldest_idle_handle_but_lease_remains_resumable()
    {
        var clock = new ManualTimeProvider();
        using var pool = CreatePool(clock, maxOpenStreams: 2, idleTimeout: TimeSpan.FromDays(1));
        var sources = Enumerable.Range(0, 3)
            .Select(_ => new TrackingStream(new byte[] { 7, 8, 9 }))
            .ToArray();
        var reopenCount = 0;
        using var first = pool.Lease(
            sources[0],
            () =>
            {
                reopenCount++;
                return new TrackingStream(new byte[] { 7, 8, 9 });
            },
            0,
            3);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        using var second = pool.Lease(sources[1], () => new TrackingStream(new byte[] { 7, 8, 9 }), 0, 3);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        using var third = pool.Lease(sources[2], () => new TrackingStream(new byte[] { 7, 8, 9 }), 0, 3);

        Assert.True(sources[0].WasDisposed);
        Assert.False(sources[1].WasDisposed);
        Assert.False(sources[2].WasDisposed);
        Assert.Equal(2, pool.OpenStreamCount);

        Assert.Equal(7, first.ReadByte());

        Assert.Equal(1, reopenCount);
        Assert.True(sources[1].WasDisposed);
        Assert.True(first.CanRead);
        Assert.Equal(2, pool.OpenStreamCount);
    }

    [Fact]
    public async Task Sweep_never_closes_a_handle_while_a_read_is_in_flight()
    {
        var clock = new ManualTimeProvider();
        using var pool = CreatePool(clock, maxOpenStreams: 1, idleTimeout: TimeSpan.FromSeconds(5));
        using var source = new BlockingReadStream();
        using var lease = pool.Lease(source, () => new TrackingStream(new byte[] { 42 }), 0, 1);
        var buffer = new byte[1];

        var read = Task.Run(() => lease.Read(buffer, 0, 1));
        Assert.True(source.ReadStarted.Wait(TimeSpan.FromSeconds(5)));

        clock.Advance(TimeSpan.FromMinutes(1));
        pool.Sweep();

        Assert.False(source.WasDisposed);
        Assert.Equal(1, pool.OpenStreamCount);

        source.AllowReadToFinish.Set();
        Assert.Equal(1, await read);
        Assert.Equal(42, buffer[0]);

        clock.Advance(TimeSpan.FromMinutes(1));
        pool.Sweep();

        Assert.True(source.WasDisposed);
        Assert.Equal(0, pool.OpenStreamCount);
    }

    [Fact]
    public async Task Pool_disposal_defers_handle_close_until_an_active_read_finishes()
    {
        var clock = new ManualTimeProvider();
        var pool = CreatePool(clock, maxOpenStreams: 1, idleTimeout: TimeSpan.FromSeconds(5));
        using var source = new BlockingReadStream();
        using var lease = pool.Lease(source, () => new TrackingStream(new byte[] { 42 }), 0, 1);

        var read = Task.Run(() => lease.Read(new byte[1], 0, 1));
        Assert.True(source.ReadStarted.Wait(TimeSpan.FromSeconds(5)));

        pool.Dispose();

        Assert.False(source.WasDisposed);

        source.AllowReadToFinish.Set();
        Assert.Equal(1, await read);
        Assert.True(source.WasDisposed);
    }

    [Fact]
    public async Task Reopening_at_the_cap_parks_another_idle_handle_before_the_read_blocks()
    {
        var clock = new ManualTimeProvider();
        using var pool = CreatePool(clock, maxOpenStreams: 1, idleTimeout: TimeSpan.FromSeconds(5));
        var firstSource = new TrackingStream(new byte[] { 42 });
        using var blockingReopen = new BlockingReadStream();
        using var first = pool.Lease(firstSource, () => blockingReopen, 0, 1);

        clock.Advance(TimeSpan.FromSeconds(6));
        pool.Sweep();
        Assert.True(firstSource.WasDisposed);
        Assert.Equal(0, pool.OpenStreamCount);

        var otherSource = new TrackingStream(new byte[] { 7 });
        using var other = pool.Lease(otherSource, () => new TrackingStream(new byte[] { 7 }), 0, 1);
        clock.Advance(TimeSpan.FromSeconds(1));

        var read = Task.Run(() => first.Read(new byte[1], 0, 1));
        Assert.True(blockingReopen.ReadStarted.Wait(TimeSpan.FromSeconds(5)));

        // The reopened lease is active, so count pressure must park the other idle handle rather
        // than waiting for this deliberately blocked read to finish.
        Assert.False(blockingReopen.WasDisposed);
        Assert.True(otherSource.WasDisposed);
        Assert.Equal(1, pool.OpenStreamCount);

        blockingReopen.AllowReadToFinish.Set();
        Assert.Equal(1, await read);
    }

    [Fact]
    public void Park_and_reopen_preserve_positions_beyond_four_gibibytes()
    {
        const long fiveGiB = 5L * 1024 * 1024 * 1024;
        const long offset = fiveGiB - 4096;
        var clock = new ManualTimeProvider();
        using var pool = CreatePool(clock, maxOpenStreams: 1, idleTimeout: TimeSpan.FromSeconds(5));
        var initial = new VirtualLargeStream(fiveGiB);
        VirtualLargeStream? reopened = null;
        using var lease = pool.Lease(
            initial,
            () => reopened = new VirtualLargeStream(fiveGiB),
            offset,
            length: 4096);

        Assert.Equal(32, lease.Read(new byte[32]));
        Assert.Equal(offset + 32, initial.Position);

        clock.Advance(TimeSpan.FromSeconds(6));
        pool.Sweep();
        Assert.True(initial.WasDisposed);

        Assert.Equal(32, lease.Read(new byte[32]));
        Assert.NotNull(reopened);
        Assert.Equal(offset + 64, reopened.Position);
    }

    [Fact]
    public async Task Concurrent_leases_reads_and_sweeps_do_not_deadlock()
    {
        // BeginRead deliberately calls OperationStarted (which trims and reaches across other lease
        // gates) OUTSIDE its own gate. If that cross-lease call were ever pulled back under the gate,
        // two threads each holding their own lease gate while trimming toward the other would deadlock.
        // Drive many threads through Lease+Read+Sweep over a tiny cap and require the run to finish.
        var clock = new ManualTimeProvider();
        using var pool = CreatePool(clock, maxOpenStreams: 2, idleTimeout: TimeSpan.FromDays(1));
        const int threads = 8;
        const int iterations = 150;
        using var start = new Barrier(threads);

        var workers = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < iterations; i++)
            {
                using var lease = pool.Lease(
                    new TrackingStream([1, 2, 3, 4]),
                    () => new TrackingStream([1, 2, 3, 4]),
                    0,
                    4);
                var buffer = new byte[4];
                var total = 0;
                int read;
                while ((read = lease.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                }
                Assert.Equal(4, total);
                if ((i & 7) == 0)
                {
                    pool.Sweep();
                }
            }
        })).ToArray();

        // WaitAsync throws TimeoutException on a stall, surfacing a deadlock as a failed test.
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));
        pool.Sweep();
        Assert.Equal(0, pool.OpenStreamCount);
    }

    [Fact]
    public async Task Overage_from_two_concurrent_reads_collapses_when_one_completes()
    {
        // The docstring promises "completion reapplies the cap". Force a standing overage that only
        // exists because every candidate is mid-read, then release one read and assert the now-idle
        // handle is parked by the OperationCompleted trim rather than the OperationStarted one.
        var clock = new ManualTimeProvider();
        using var pool = CreatePool(clock, maxOpenStreams: 1, idleTimeout: TimeSpan.FromSeconds(5));
        using var firstReopen = new BlockingReadStream();
        using var secondReopen = new BlockingReadStream();
        using var first = pool.Lease(new TrackingStream([1]), () => firstReopen, 0, 1);
        clock.Advance(TimeSpan.FromSeconds(6));
        pool.Sweep();
        using var second = pool.Lease(new TrackingStream([1]), () => secondReopen, 0, 1);
        clock.Advance(TimeSpan.FromSeconds(6));
        pool.Sweep();
        Assert.Equal(0, pool.OpenStreamCount);

        var firstRead = Task.Run(() => first.Read(new byte[1], 0, 1));
        Assert.True(firstReopen.ReadStarted.Wait(TimeSpan.FromSeconds(5)));
        var secondRead = Task.Run(() => second.Read(new byte[1], 0, 1));
        Assert.True(secondReopen.ReadStarted.Wait(TimeSpan.FromSeconds(5)));

        // Both leases reopened and are blocked in-read: the cap is genuinely exceeded.
        Assert.Equal(2, pool.OpenStreamCount);

        firstReopen.AllowReadToFinish.Set();
        Assert.Equal(1, await firstRead);

        // Completing the first read must reapply the cap and park the now-idle first lease.
        Assert.Equal(1, pool.OpenStreamCount);
        Assert.True(firstReopen.WasDisposed);
        Assert.False(secondReopen.WasDisposed);

        secondReopen.AllowReadToFinish.Set();
        Assert.Equal(1, await secondRead);
    }

    [Fact]
    public void Scheduled_timer_callback_parks_an_idle_lease_without_a_manual_sweep()
    {
        // Every other test calls Sweep() by hand; this one verifies the constructor actually wires the
        // periodic timer to Sweep with the right state and interval, the sole automatic reclaim path.
        var clock = new ControllableTimeProvider();
        var sweepInterval = TimeSpan.FromSeconds(5);
        using var pool = new MediaStreamLeasePool(
            maxOpenStreams: 4,
            idleTimeout: TimeSpan.FromSeconds(10),
            sweepInterval: sweepInterval,
            timeProvider: clock);
        var initial = new TrackingStream([1, 2, 3]);
        using var lease = pool.Lease(initial, () => new TrackingStream([1, 2, 3]), 0, 3);
        Assert.Equal(1, pool.OpenStreamCount);
        Assert.Equal(sweepInterval, clock.LastTimerPeriod);

        clock.Advance(TimeSpan.FromSeconds(11));
        clock.FireTimers();

        Assert.True(initial.WasDisposed);
        Assert.Equal(0, pool.OpenStreamCount);
        Assert.True(lease.CanRead); // parked, not disposed
    }

    [Fact]
    public void A_short_read_before_the_window_end_disposes_the_lease()
    {
        // A truncated resource can hand back 0 while the logical window is unfinished. CompleteRead(0)
        // must terminate and release the handle rather than leaving it open and re-reading forever.
        var clock = new ManualTimeProvider();
        using var pool = CreatePool(clock, maxOpenStreams: 4, idleTimeout: TimeSpan.FromSeconds(10));
        var source = new ShortReadStream(reportedLength: 10, bytesBeforeZero: 2);
        using var lease = pool.Lease(source, () => new ShortReadStream(10, 2), offset: 0, length: 4);
        var buffer = new byte[4];

        Assert.Equal(1, lease.Read(buffer, 0, 4));
        Assert.Equal(1, lease.Read(buffer, 0, 4));
        Assert.Equal(1, pool.OpenStreamCount);

        // Underlying now yields 0 while the logical position (2) is short of the window length (4).
        Assert.Equal(0, lease.Read(buffer, 0, 4));

        Assert.True(source.WasDisposed);
        Assert.Equal(0, pool.OpenStreamCount);
        Assert.False(lease.CanRead);
    }

    [Fact]
    public void Reading_at_end_returns_zero_and_keeps_the_stream_usable()
    {
        // Reaching logical EOF frees the handle but must not dispose the lease: a further read returns
        // 0 (not ObjectDisposedException), Position/Length/Seek stay usable, and a seek back re-reads.
        var clock = new ManualTimeProvider();
        using var pool = CreatePool(clock, maxOpenStreams: 4, idleTimeout: TimeSpan.FromSeconds(10));
        var initial = new TrackingStream([10, 11, 12, 13]);
        TrackingStream? reopened = null;
        using var lease = pool.Lease(
            initial,
            () => reopened = new TrackingStream([10, 11, 12, 13]),
            offset: 1,
            length: 2);
        var buffer = new byte[2];

        Assert.Equal(2, lease.Read(buffer));
        Assert.Equal(new byte[] { 11, 12 }, buffer);

        Assert.Equal(0, lease.Read(buffer)); // logical EOF frees the handle
        Assert.True(initial.WasDisposed);
        Assert.Equal(0, pool.OpenStreamCount);

        Assert.Equal(0, lease.Read(buffer)); // repeated read at EOF stays graceful
        Assert.True(lease.CanRead);
        Assert.True(lease.CanSeek);
        Assert.Equal(2, lease.Length);
        Assert.Equal(2, lease.Position);

        lease.Position = 0;
        Assert.Equal(2, lease.Read(buffer));
        Assert.Equal(new byte[] { 11, 12 }, buffer);
        Assert.NotNull(reopened);
    }

    [Fact]
    public void Reading_a_parked_lease_after_pool_disposal_throws()
    {
        var clock = new ManualTimeProvider();
        var pool = CreatePool(clock, maxOpenStreams: 1, idleTimeout: TimeSpan.FromSeconds(5));
        var source = new TrackingStream([1, 2, 3]);
        using var lease = pool.Lease(source, () => new TrackingStream([1, 2, 3]), 0, 3);

        clock.Advance(TimeSpan.FromSeconds(6));
        pool.Sweep();
        Assert.True(source.WasDisposed); // parked

        pool.Dispose();

        Assert.Throws<ObjectDisposedException>(() => lease.Read(new byte[1], 0, 1));
    }

    private static MediaStreamLeasePool CreatePool(
        TimeProvider clock,
        int maxOpenStreams,
        TimeSpan idleTimeout)
        => new(
            maxOpenStreams,
            idleTimeout,
            sweepInterval: TimeSpan.FromDays(1),
            timeProvider: clock);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }

    private sealed class TrackingStream(byte[] data) : MemoryStream(data)
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public ManualResetEventSlim ReadStarted { get; } = new();
        public ManualResetEventSlim AllowReadToFinish { get; } = new();
        public bool WasDisposed { get; private set; }

        public override bool CanRead => !WasDisposed;
        public override bool CanSeek => !WasDisposed;
        public override bool CanWrite => false;
        public override long Length => 1;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            ReadStarted.Set();
            if (!AllowReadToFinish.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The test did not release the blocking read.");
            }

            if (Position >= Length || count == 0)
            {
                return 0;
            }

            buffer[offset] = 42;
            Position++;
            return 1;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return Position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            ReadStarted.Dispose();
            AllowReadToFinish.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class VirtualLargeStream(long length) : Stream
    {
        public bool WasDisposed { get; private set; }
        public override bool CanRead => !WasDisposed;
        public override bool CanSeek => !WasDisposed;
        public override bool CanWrite => false;
        public override long Length { get; } = length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            var read = (int)Math.Min(count, Length - Position);
            Array.Clear(buffer, offset, read);
            Position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return Position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>A clock whose timers fire only when the test drives <see cref="FireTimers"/>.</summary>
    private sealed class ControllableTimeProvider : TimeProvider
    {
        private readonly List<FakeTimer> _timers = [];
        private DateTimeOffset _utcNow = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

        public TimeSpan? LastTimerPeriod { get; private set; }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            LastTimerPeriod = period;
            var timer = new FakeTimer(callback, state);
            lock (_timers)
            {
                _timers.Add(timer);
            }
            return timer;
        }

        public void FireTimers()
        {
            FakeTimer[] snapshot;
            lock (_timers)
            {
                snapshot = [.. _timers];
            }
            foreach (var timer in snapshot)
            {
                timer.Fire();
            }
        }

        private sealed class FakeTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;

            public void Fire()
            {
                if (!_disposed)
                {
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                _disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Reports a large length but stops yielding bytes early, mimicking a truncated resource.</summary>
    private sealed class ShortReadStream(long reportedLength, int bytesBeforeZero) : Stream
    {
        public bool WasDisposed { get; private set; }
        public override bool CanRead => !WasDisposed;
        public override bool CanSeek => !WasDisposed;
        public override bool CanWrite => false;
        public override long Length { get; } = reportedLength;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            if (count == 0 || Position >= bytesBeforeZero)
            {
                return 0;
            }

            buffer[offset] = 9;
            Position++;
            return 1;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return Position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
