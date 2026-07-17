using BgRecorder.Logs;
using Xunit;

namespace BgRecorder.Logs.Tests;

public sealed class LogHealthMonitorTests
{
    // A game start whose prefix a patch broke: logger name and CREATE_GAME survive, nothing parses.
    private const string BrokenGameStart =
        "X 99/99 GameState.DebugPrintPower() - CREATE_GAME";

    // Ordinary mid-match traffic that legitimately yields no events (the overwhelming majority of
    // GameState lines — FULL_ENTITY, most TAG_CHANGEs, combat damage…).
    private const string SilentMidMatchLine =
        "D 20:01:02.0000000 GameState.DebugPrintPower() - TAG_CHANGE Entity=x tag=DAMAGE value=4";

    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    [Fact]
    public void An_unparsed_game_start_followed_by_silent_traffic_alerts_exactly_once()
    {
        var monitor = new LogHealthMonitor(minLines: 5, window: TimeSpan.FromMinutes(3));

        Assert.Null(monitor.OnSilentLine(BrokenGameStart, T0)); // arms, never alerts by itself

        string? alert = null;
        for (int i = 1; i <= 5; i++)
        {
            alert = monitor.OnSilentLine(SilentMidMatchLine, T0.AddMinutes(i));
        }

        Assert.NotNull(alert);
        Assert.Contains("game patch", alert);

        // Latched: continued broken traffic must not spam.
        Assert.Null(monitor.OnSilentLine(SilentMidMatchLine, T0.AddMinutes(30)));
    }

    [Fact]
    public void Mid_match_silent_stretches_never_alert_without_an_unparsed_game_start()
    {
        // Regression for the measured false positive: a healthy late-game recruit phase produces up to
        // ~19,000 event-free GameState lines over ~4 minutes. Without a silent CREATE_GAME the
        // detector must stay unarmed no matter how much silent traffic flows.
        var monitor = new LogHealthMonitor(minLines: 200, window: TimeSpan.FromMinutes(3));

        for (int i = 0; i < 20_000; i++)
        {
            Assert.Null(monitor.OnSilentLine(SilentMidMatchLine, T0.AddSeconds(i)));
        }
    }

    [Fact]
    public void Attaching_mid_match_never_alerts_before_the_next_game_start()
    {
        // Regression for the mid-match-startup false positive: the tail seeks to end, CREATE_GAME was
        // missed, so the parser legitimately yields nothing for the rest of that match. No alert —
        // and the NEXT game's unparsed start is what may legitimately arm it.
        var monitor = new LogHealthMonitor(minLines: 3, window: TimeSpan.Zero);

        for (int i = 0; i < 5_000; i++)
        {
            Assert.Null(monitor.OnSilentLine(SilentMidMatchLine, T0.AddSeconds(i)));
        }

        Assert.Null(monitor.OnSilentLine(BrokenGameStart, T0.AddHours(2))); // arms now
        Assert.Null(monitor.OnSilentLine(SilentMidMatchLine, T0.AddHours(2).AddSeconds(1)));
        Assert.Null(monitor.OnSilentLine(SilentMidMatchLine, T0.AddHours(2).AddSeconds(2)));
        Assert.NotNull(monitor.OnSilentLine(SilentMidMatchLine, T0.AddHours(2).AddSeconds(3)));
    }

    [Fact]
    public void A_burst_after_arming_does_not_alert_before_the_window_elapses()
    {
        var monitor = new LogHealthMonitor(minLines: 5, window: TimeSpan.FromMinutes(3));

        monitor.OnSilentLine(BrokenGameStart, T0);
        for (int i = 0; i < 500; i++)
        {
            Assert.Null(monitor.OnSilentLine(SilentMidMatchLine, T0.AddSeconds(1)));
        }
    }

    [Fact]
    public void Time_after_arming_does_not_alert_below_the_line_floor()
    {
        var monitor = new LogHealthMonitor(minLines: 200, window: TimeSpan.FromMinutes(3));

        monitor.OnSilentLine(BrokenGameStart, T0);
        Assert.Null(monitor.OnSilentLine(SilentMidMatchLine, T0.AddHours(2))); // 1 line, hours later
    }

    [Fact]
    public void Non_gamestate_noise_neither_arms_nor_counts()
    {
        var monitor = new LogHealthMonitor(minLines: 2, window: TimeSpan.Zero);

        Assert.Null(monitor.OnSilentLine("LoadingScreen.OnScenePreUnload() - CREATE_GAME", T0));
        Assert.Null(monitor.OnSilentLine("PowerTaskList.DebugPrintPower() - CREATE_GAME", T0.AddMinutes(5)));
        Assert.Null(monitor.OnSilentLine("random noise", T0.AddMinutes(9)));
    }

    [Fact]
    public void Events_disarm_and_a_second_broken_stretch_realerts()
    {
        var monitor = new LogHealthMonitor(minLines: 2, window: TimeSpan.FromMinutes(1));

        monitor.OnSilentLine(BrokenGameStart, T0);
        monitor.OnSilentLine(SilentMidMatchLine, T0.AddMinutes(1));
        Assert.NotNull(monitor.OnSilentLine(SilentMidMatchLine, T0.AddMinutes(2)));

        monitor.OnEvents(1); // a hotfix landed: events flow again

        // Silent traffic alone must not re-alert — the detector is disarmed…
        Assert.Null(monitor.OnSilentLine(SilentMidMatchLine, T0.AddMinutes(10)));
        // …until another game start goes unparsed.
        monitor.OnSilentLine(BrokenGameStart, T0.AddMinutes(11));
        monitor.OnSilentLine(SilentMidMatchLine, T0.AddMinutes(12));
        Assert.NotNull(monitor.OnSilentLine(SilentMidMatchLine, T0.AddMinutes(13)));
    }

    [Fact]
    public void Reset_disarms_across_a_session_change()
    {
        var monitor = new LogHealthMonitor(minLines: 2, window: TimeSpan.Zero);

        monitor.OnSilentLine(BrokenGameStart, T0); // armed in session A

        monitor.Reset(); // session B begins

        Assert.Null(monitor.OnSilentLine(SilentMidMatchLine, T0.AddMinutes(1)));
        Assert.Null(monitor.OnSilentLine(SilentMidMatchLine, T0.AddMinutes(2))); // would alert if still armed
    }
}
