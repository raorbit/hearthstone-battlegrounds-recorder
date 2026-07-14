using System.Diagnostics;
using BgRecorder.Core.Events;
using BgRecorder.Logs;
using Xunit;

namespace BgRecorder.Logs.Tests;

/// <summary>
/// End-to-end wiring test: a sanitized fixture is replayed into a fake install dir with
/// <see cref="TestFeedWriter"/> and read back through the full discovery + FileShare.ReadWrite tail + parse
/// path of <see cref="GameEventSource"/>. The streamed events (minus the leading LogSessionChanged) must
/// equal what <see cref="PowerLogParser"/> produces when fed the same fixture directly.
/// </summary>
public sealed class GameEventSourceEndToEndTests : IDisposable
{
    private readonly string _install;

    public GameEventSourceEndToEndTests()
    {
        _install = Path.Combine(Path.GetTempPath(), "bgrec-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_install);
    }

    public void Dispose()
    {
        try { Directory.Delete(_install, recursive: true); } catch { /* best effort */ }
    }

    // Reproduce exactly how GameEventSource seeds its parser (folder-name local time + machine local offset).
    private static DateTimeOffset SourceSeed()
    {
        var local = new DateTime(2026, 7, 13, 21, 56, 22);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    [Fact]
    public async Task Fed_fixture_round_trips_through_discovery_tail_and_parse()
    {
        var seed = SourceSeed();
        var writer = new TestFeedWriter(_install, seed);

        var captured = new List<GameEvent>();
        var gate = new object();
        await using var source = new GameEventSource(
            _install,
            pollInterval: TimeSpan.FromMilliseconds(25),
            rediscoverInterval: TimeSpan.FromMilliseconds(500));
        source.EventReceived += e => { lock (gate) captured.Add(e); };

        await source.StartAsync(CancellationToken.None);

        // Wait until the watcher has opened the (empty) log at end, so nothing we feed is missed.
        await WaitUntil(() => Snapshot(captured, gate).OfType<LogSessionChanged>().Any(), TimeSpan.FromSeconds(5));

        writer.AppendFixture(Fixtures.FixturePath(3));

        // Let the tailer drain the whole fixture (wait for the event stream to stop growing), then dispose —
        // DisposeAsync flushes, which emits the deferred MatchEnded for the completed match.
        await WaitForStable(captured, gate, TimeSpan.FromSeconds(15));
        await source.DisposeAsync();

        var live = Snapshot(captured, gate);

        // First event is the session switch; the rest must match a direct parse of the same fixture.
        Assert.IsType<LogSessionChanged>(live[0]);
        Assert.Contains(live, e => e is MatchEnded);
        var liveMatch = live.Skip(1).ToList();

        var direct = new PowerLogParser(seed);
        var expected = new List<GameEvent>();
        foreach (var line in File.ReadLines(Fixtures.FixturePath(3)))
            expected.AddRange(direct.Feed(line));
        expected.AddRange(direct.Flush());

        Assert.Equal(expected.Count, liveMatch.Count);
        Assert.Equal(expected, liveMatch); // GameEvent records compare by value (incl. instant)
    }

    [Fact]
    public async Task Starts_cleanly_and_stops_when_no_log_exists_yet()
    {
        // Game not running: no session folder. The source must start, idle without throwing, and stop.
        var captured = new List<GameEvent>();
        var gate = new object();
        await using var source = new GameEventSource(
            _install,
            pollInterval: TimeSpan.FromMilliseconds(25),
            rediscoverInterval: TimeSpan.FromMilliseconds(100));
        source.EventReceived += e => { lock (gate) captured.Add(e); };

        await source.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await source.DisposeAsync();

        Assert.Empty(Snapshot(captured, gate)); // nothing discovered, nothing emitted, no exception
    }

    private static List<GameEvent> Snapshot(List<GameEvent> list, object gate)
    {
        lock (gate) return new List<GameEvent>(list);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout) throw new TimeoutException("condition not met within " + timeout);
            await Task.Delay(20);
        }
    }

    /// <summary>Wait until the captured event count has been unchanged for ~200 ms (the tailer has drained).</summary>
    private static async Task WaitForStable(List<GameEvent> list, object gate, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        int last = -1;
        int stableFor = 0;
        while (sw.Elapsed < timeout)
        {
            int count = Snapshot(list, gate).Count;
            stableFor = count == last ? stableFor + 1 : 0;
            last = count;
            if (count > 0 && stableFor >= 10) return; // ~10 * 20 ms unchanged
            await Task.Delay(20);
        }
    }
}
