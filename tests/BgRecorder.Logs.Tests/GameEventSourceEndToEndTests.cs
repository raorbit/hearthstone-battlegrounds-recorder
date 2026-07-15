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

    /// <summary>
    /// Regression for the log-session rollback fix: the Power.log already holds a complete match, and
    /// the FIRST open fails (transient IO) before the retry succeeds. Session state is committed only
    /// after a successful open, so the retry re-enters the new-session branch and seeks to END — the
    /// pre-existing match is NOT replayed. Before the fix, the retry fell into the plain reopen at
    /// offset 0 and replayed the whole log, with no LogSessionChanged emitted.
    /// </summary>
    [Fact]
    public async Task First_discovery_open_failure_does_not_replay_the_existing_log()
    {
        var seed = SourceSeed();
        var writer = new TestFeedWriter(_install, seed);
        writer.AppendFixture(Fixtures.FixturePath(3)); // a full match sitting on disk before we start

        int opens = 0;
        FileStream FlakyOpen(string path)
        {
            if (Interlocked.Increment(ref opens) == 1)
                throw new IOException("simulated transient open failure");
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        var captured = new List<GameEvent>();
        var gate = new object();
        await using var source = new GameEventSource(
            _install,
            pollInterval: TimeSpan.FromMilliseconds(25),
            rediscoverInterval: TimeSpan.FromMilliseconds(500),
            FlakyOpen);
        source.EventReceived += e => { lock (gate) captured.Add(e); };

        await source.StartAsync(CancellationToken.None);
        await WaitUntil(() => Snapshot(captured, gate).OfType<LogSessionChanged>().Any(), TimeSpan.FromSeconds(5));
        await WaitForStable(captured, gate, TimeSpan.FromSeconds(3));
        await source.DisposeAsync();

        var live = Snapshot(captured, gate);
        Assert.True(opens >= 2, $"the first open should fail and the retry succeed; opens={opens}");
        Assert.Single(live.OfType<LogSessionChanged>());     // announced exactly once
        Assert.DoesNotContain(live, e => e is MatchStarted);  // existing match not replayed
        Assert.DoesNotContain(live, e => e is MatchEnded);
    }

    /// <summary>
    /// Regression for the same fix on the session-change branch: running on session A, a newer folder
    /// B appears and the FIRST open of B fails (transient IO). Because state is committed only after a
    /// successful open, the retry still announces B (LogSessionChanged) and tails it. Before the fix,
    /// state was committed to B before the open, so the retry fell into the plain reopen at A's stale
    /// offset and B was never announced.
    /// </summary>
    [Fact]
    public async Task Session_change_open_failure_still_announces_the_new_session()
    {
        var seedA = SourceSeed();
        var writerA = new TestFeedWriter(_install, seedA);
        writerA.AppendFixture(Fixtures.FixturePath(3)); // A already has a match (seeked past on start)

        var seedB = seedA.AddMinutes(30); // newer folder name -> discovered as the new session
        var folderBName = "Hearthstone_" + seedB.ToString("yyyy_MM_dd_HH_mm_ss");

        int failedB = 0;
        FileStream FlakyOpen(string path)
        {
            if (path.Contains(folderBName, StringComparison.OrdinalIgnoreCase)
                && Interlocked.Increment(ref failedB) == 1)
                throw new IOException("simulated transient open failure on the new session");
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        var captured = new List<GameEvent>();
        var gate = new object();
        await using var source = new GameEventSource(
            _install,
            pollInterval: TimeSpan.FromMilliseconds(25),
            rediscoverInterval: TimeSpan.FromMilliseconds(200),
            FlakyOpen);
        source.EventReceived += e => { lock (gate) captured.Add(e); };

        await source.StartAsync(CancellationToken.None);
        await WaitUntil(() => Snapshot(captured, gate).OfType<LogSessionChanged>().Any(), TimeSpan.FromSeconds(5));

        // Bring session B online; the switch's first open fails once, then the retry must announce B.
        var writerB = new TestFeedWriter(_install, seedB);
        await WaitUntil(() => Snapshot(captured, gate).OfType<LogSessionChanged>().Count() >= 2, TimeSpan.FromSeconds(8));
        Assert.True(failedB >= 1, "the first open of the new session should have failed at least once");

        // B was announced (and seeked to its current end); a match appended now must be tailed live.
        // MatchStarted is emitted inline; the completed match's MatchEnded is deferred until flush.
        writerB.AppendFixture(Fixtures.FixturePath(3));
        await WaitUntil(() => Snapshot(captured, gate).OfType<MatchStarted>().Any(), TimeSpan.FromSeconds(15));
        await WaitForStable(captured, gate, TimeSpan.FromSeconds(5));
        await source.DisposeAsync();

        var live = Snapshot(captured, gate);
        Assert.True(live.OfType<LogSessionChanged>().Count() >= 2, "the new session must be announced after the transient failure");
        Assert.Contains(live, e => e is MatchStarted); // B was tailed from a correct offset
        Assert.Contains(live, e => e is MatchEnded);   // and its match closed out on flush
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
