using System.Diagnostics;
using BgRecorder.Core.Events;
using BgRecorder.Logs;
using Xunit;

namespace BgRecorder.Logs.Tests;

/// <summary>
/// Wiring test for the patch-day tripwire: unparseable game traffic flowing through the REAL
/// discovery + tail path must raise exactly one <see cref="GameEventSource.HealthAlert"/>, and a
/// healthy line afterwards must clear the latch.
/// </summary>
public sealed class GameEventSourceHealthTests : IDisposable
{
    private readonly string _install;

    public GameEventSourceHealthTests()
    {
        _install = Path.Combine(Path.GetTempPath(), "bgrec-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_install);
    }

    public void Dispose()
    {
        try { Directory.Delete(_install, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Unparseable_game_traffic_raises_one_alert_and_recovers_on_a_healthy_line()
    {
        var seed = new DateTimeOffset(new DateTime(2026, 7, 16, 20, 0, 0),
            TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 7, 16, 20, 0, 0)));
        var writer = new TestFeedWriter(_install, seed);

        var alerts = new List<string>();
        var events = new List<GameEvent>();
        var gate = new object();

        await using var source = new GameEventSource(
            _install,
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromMilliseconds(500),
            path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
            new LogHealthMonitor(minLines: 10, window: TimeSpan.Zero));
        source.HealthAlert += a => { lock (gate) alerts.Add(a); };
        source.EventReceived += e => { lock (gate) events.Add(e); };

        await source.StartAsync(CancellationToken.None);
        await WaitUntil(() => Snapshot(events, gate).OfType<LogSessionChanged>().Any(), TimeSpan.FromSeconds(5));

        // A patch changed the line prefix: the logger name survives but nothing parses.
        var broken = Enumerable.Repeat("BROKEN 20:00:00 GameState.DebugPrintPower() - CREATE_GAME", 25);
        writer.AppendLines(broken);
        await WaitUntil(() => Snapshot(alerts, gate).Count > 0, TimeSpan.FromSeconds(5));

        // More broken traffic must not re-alert while latched.
        writer.AppendLines(broken);
        await Task.Delay(300);
        Assert.Single(Snapshot(alerts, gate));
        Assert.Contains("game patch", Snapshot(alerts, gate)[0]);

        // The parser understands a healthy line again (hotfix landed): events flow, latch clears.
        writer.AppendLine("D 20:00:01.0000000 GameState.DebugPrintPower() - CREATE_GAME");
        await WaitUntil(() => Snapshot(events, gate).OfType<MatchStarted>().Any(), TimeSpan.FromSeconds(5));

        // And a fresh broken stretch alerts AGAIN — the tripwire re-arms after recovery.
        writer.AppendLines(broken);
        await WaitUntil(() => Snapshot(alerts, gate).Count == 2, TimeSpan.FromSeconds(5));
    }

    private static List<T> Snapshot<T>(List<T> list, object gate)
    {
        lock (gate) return new List<T>(list);
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
}
