using BgRecorder.Core.Data;
using BgRecorder.Core.Events;
using BgRecorder.Session;
using Xunit;

namespace BgRecorder.Session.Tests;

public sealed class DiskSafetyTests
{
    private const long Gib = 1L << 30;

    private readonly FakeRepository _repository = new();
    private readonly FakeFreeSpaceProbe _probe = new();

    private DiskSafety CreateSut(TimeSpan? poll = null)
        => new(@"C:\anywhere\staging", _repository, _probe, poll);

    private static MatchRecord MatchOfSize(long sizeBytes, int daysAgo) => new()
    {
        StartedAt = DateTimeOffset.Now.AddDays(-daysAgo),
        GameType = BgGameType.Solo,
        VideoStatus = VideoStatus.Complete,
        VideoSizeBytes = sizeBytes,
    };

    // ---------------------------------------------------------------- floor / arm check

    [Fact]
    public void NoHistory_FloorIsTenGib()
    {
        Assert.Equal(DiskSafety.MinimumFloorBytes, CreateSut().ComputeFloorBytes());
        Assert.Equal(10L * Gib, DiskSafety.MinimumFloorBytes);
    }

    [Fact]
    public void CanArm_WhenFreeSpaceAboveFloor()
    {
        _probe.Free = 11 * Gib;
        var result = CreateSut().CheckCanArm();
        Assert.True(result.CanArm);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void RefusesArm_WhenFreeSpaceBelowFloor_WithReason()
    {
        _probe.Free = 9 * Gib;
        var result = CreateSut().CheckCanArm();
        Assert.False(result.CanArm);
        Assert.NotNull(result.Reason);
        Assert.Contains("safety floor", result.Reason);
    }

    [Fact]
    public void History_FloorIsTwiceRollingAverage()
    {
        _repository.Matches = [MatchOfSize(8 * Gib, 1), MatchOfSize(8 * Gib, 2)];
        Assert.Equal(16 * Gib, CreateSut().ComputeFloorBytes());

        _probe.Free = 12 * Gib; // above 10 GiB minimum but below 2x avg
        Assert.False(CreateSut().CheckCanArm().CanArm);

        _probe.Free = 17 * Gib;
        Assert.True(CreateSut().CheckCanArm().CanArm);
    }

    [Fact]
    public void SmallHistory_NeverDropsBelowTenGibMinimum()
    {
        _repository.Matches = [MatchOfSize(1 * Gib, 1)];
        Assert.Equal(DiskSafety.MinimumFloorBytes, CreateSut().ComputeFloorBytes());
    }

    [Fact]
    public void RollingAverage_UsesOnlyTheNewestTwentyMatches()
    {
        // 20 newest at 8 GiB, older monsters at 100 GiB must not count.
        var matches = new List<MatchRecord>();
        for (var i = 0; i < 20; i++)
        {
            matches.Add(MatchOfSize(8 * Gib, i));
        }
        matches.Add(MatchOfSize(100 * Gib, 400));
        _repository.Matches = matches;
        Assert.Equal(16 * Gib, CreateSut().ComputeFloorBytes());
    }

    [Fact]
    public void RepositoryFailure_DegradesToTenGibFloor()
    {
        var sut = new DiskSafety(@"C:\anywhere\staging", new ThrowingRepository(), _probe);
        Assert.Equal(DiskSafety.MinimumFloorBytes, sut.ComputeFloorBytes());
    }

    [Fact]
    public void ProbeFailure_RefusesArm()
    {
        _probe.Throws = new IOException("no volume");
        var result = CreateSut().CheckCanArm();
        Assert.False(result.CanArm);
        Assert.Contains("free space", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- watchdog

    [Fact]
    public async Task Watchdog_FiresOnceWhenBelowFloor()
    {
        _probe.Free = 1 * Gib;
        var fired = 0;
        using var handle = CreateSut(TimeSpan.FromMilliseconds(20)).StartWatchdog(() => Interlocked.Increment(ref fired));

        await WaitUntilAsync(() => Volatile.Read(ref fired) >= 1);
        await Task.Delay(150); // several more poll intervals
        Assert.Equal(1, Volatile.Read(ref fired)); // exactly once
    }

    [Fact]
    public async Task Watchdog_DoesNotFireAboveFloor()
    {
        _probe.Free = 50 * Gib;
        var fired = 0;
        using var handle = CreateSut(TimeSpan.FromMilliseconds(20)).StartWatchdog(() => Interlocked.Increment(ref fired));

        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref fired));
    }

    [Fact]
    public async Task Watchdog_StopsWhenDisposed()
    {
        _probe.Free = 50 * Gib;
        var fired = 0;
        var handle = CreateSut(TimeSpan.FromMilliseconds(20)).StartWatchdog(() => Interlocked.Increment(ref fired));
        handle.Dispose();

        _probe.Free = 1 * Gib; // now below floor, but the watchdog is gone
        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref fired));
    }

    [Fact]
    public async Task Watchdog_SurvivesTransientProbeFailures()
    {
        _probe.Throws = new IOException("transient");
        var fired = 0;
        using var handle = CreateSut(TimeSpan.FromMilliseconds(20)).StartWatchdog(() => Interlocked.Increment(ref fired));

        await Task.Delay(100);
        _probe.Throws = null;
        _probe.Free = 1 * Gib;
        await WaitUntilAsync(() => Volatile.Read(ref fired) >= 1);
        Assert.Equal(1, Volatile.Read(ref fired));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Condition not met in time.");
            }
            await Task.Delay(10);
        }
    }

    private sealed class ThrowingRepository : IMatchRepository
    {
        public Task InitializeAsync(CancellationToken ct = default) => throw new InvalidOperationException("db down");

        public Task<long> InsertMatchAsync(MatchRecord match, IReadOnlyList<MarkerRecord> markers, CancellationToken ct = default)
            => throw new InvalidOperationException("db down");

        public Task<bool> MatchExistsBySessionAsync(string sessionId, CancellationToken ct = default)
            => throw new InvalidOperationException("db down");

        public Task UpdateVideoStatusAsync(long matchId, VideoStatus status, CancellationToken ct = default)
            => throw new InvalidOperationException("db down");

        public Task<IReadOnlyList<MatchRecord>> ListMatchesAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("db down");

        public Task<MatchDetailRecord?> GetMatchAsync(long matchId, CancellationToken ct = default)
            => throw new InvalidOperationException("db down");

        public Task UpdateStarredAsync(long matchId, bool starred, CancellationToken ct = default)
            => throw new InvalidOperationException("db down");
    }
}
