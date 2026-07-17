using BgRecorder.Logs;
using Xunit;

namespace BgRecorder.Logs.Tests;

public sealed class LogHealthMonitorTests
{
    private const string BrokenGameStateLine =
        "X 99/99 GameState.DebugPrintPower() - CREATE_GAME"; // logger name present, prefix unparseable

    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Sustained_unparsed_game_traffic_alerts_exactly_once()
    {
        var monitor = new LogHealthMonitor(minLines: 5, window: TimeSpan.FromMinutes(3));

        string? alert = null;
        for (int i = 0; i < 5; i++)
        {
            alert = monitor.OnSilentLine(BrokenGameStateLine, T0.AddMinutes(i)); // 5th line is 4 min in
        }

        Assert.NotNull(alert);
        Assert.Contains("game patch", alert);

        // Latched: continued broken traffic must not spam.
        Assert.Null(monitor.OnSilentLine(BrokenGameStateLine, T0.AddMinutes(10)));
    }

    [Fact]
    public void A_line_burst_alone_does_not_alert_before_the_window_elapses()
    {
        var monitor = new LogHealthMonitor(minLines: 5, window: TimeSpan.FromMinutes(3));

        for (int i = 0; i < 500; i++)
        {
            Assert.Null(monitor.OnSilentLine(BrokenGameStateLine, T0.AddSeconds(1))); // all within seconds
        }
    }

    [Fact]
    public void Time_alone_does_not_alert_below_the_line_floor()
    {
        var monitor = new LogHealthMonitor(minLines: 200, window: TimeSpan.FromMinutes(3));

        Assert.Null(monitor.OnSilentLine(BrokenGameStateLine, T0));
        Assert.Null(monitor.OnSilentLine(BrokenGameStateLine, T0.AddHours(2))); // 2 lines, hours apart
    }

    [Fact]
    public void Non_gamestate_noise_never_counts()
    {
        var monitor = new LogHealthMonitor(minLines: 2, window: TimeSpan.Zero);

        Assert.Null(monitor.OnSilentLine("D 12:00:00.0000000 LoadingScreen.OnScenePreUnload() - x", T0));
        Assert.Null(monitor.OnSilentLine("random noise", T0.AddMinutes(5)));
        Assert.Null(monitor.OnSilentLine("PowerTaskList.DebugPrintPower() - y", T0.AddMinutes(9)));
    }

    [Fact]
    public void Events_reset_the_window_and_clear_the_latch_so_a_second_break_realerts()
    {
        var monitor = new LogHealthMonitor(minLines: 3, window: TimeSpan.FromMinutes(1));

        // First broken stretch → alert.
        monitor.OnSilentLine(BrokenGameStateLine, T0);
        monitor.OnSilentLine(BrokenGameStateLine, T0.AddMinutes(1));
        Assert.NotNull(monitor.OnSilentLine(BrokenGameStateLine, T0.AddMinutes(2)));

        // Parser recovers (say a partial patch, or a hotfix landed): events flow.
        monitor.OnEvents(1);

        // A fresh broken stretch must alert again from a fresh window.
        Assert.Null(monitor.OnSilentLine(BrokenGameStateLine, T0.AddMinutes(10)));
        Assert.Null(monitor.OnSilentLine(BrokenGameStateLine, T0.AddMinutes(11)));
        Assert.NotNull(monitor.OnSilentLine(BrokenGameStateLine, T0.AddMinutes(12)));
    }

    [Fact]
    public void Healthy_traffic_with_interleaved_events_never_alerts()
    {
        var monitor = new LogHealthMonitor(minLines: 3, window: TimeSpan.Zero);

        for (int i = 0; i < 100; i++)
        {
            Assert.Null(monitor.OnSilentLine(BrokenGameStateLine, T0.AddMinutes(i)));
            if (i % 2 == 0)
            {
                monitor.OnEvents(1); // events keep resetting the count below the floor
            }
        }
    }
}
