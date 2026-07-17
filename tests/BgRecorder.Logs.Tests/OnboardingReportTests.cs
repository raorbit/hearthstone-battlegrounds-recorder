using BgRecorder.Logs;
using Xunit;

namespace BgRecorder.Logs.Tests;

public sealed class OnboardingReportTests
{
    [Theory]
    [InlineData(LogConfigOutcome.Created, true, true)]
    [InlineData(LogConfigOutcome.Updated, true, true)]
    [InlineData(LogConfigOutcome.AlreadyCompliant, true, false)] // nothing changed → nothing to pick up
    [InlineData(LogConfigOutcome.Created, false, false)]         // no game running → next launch reads it
    [InlineData(LogConfigOutcome.Updated, false, false)]
    [InlineData(LogConfigOutcome.AlreadyCompliant, false, false)]
    public void Restart_is_owed_exactly_when_the_config_changed_under_a_running_game(
        LogConfigOutcome outcome, bool gameRunning, bool expected)
    {
        var report = OnboardingReport.From(new LogConfigResult(outcome, null), gameRunning);

        Assert.Equal(expected, report.GameRestartNeeded);
        Assert.Null(report.LogConfigError);
        Assert.NotNull(report.LogConfig);
    }

    [Fact]
    public void A_failed_ensure_reports_the_error_and_never_owes_a_restart()
    {
        var report = OnboardingReport.Failed("access denied");

        Assert.Null(report.LogConfig);
        Assert.Equal("access denied", report.LogConfigError);
        Assert.False(report.GameRestartNeeded);
    }
}
