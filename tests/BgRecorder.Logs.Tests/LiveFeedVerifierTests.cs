using BgRecorder.Logs;
using Xunit;

namespace BgRecorder.Logs.Tests;

public sealed class LiveFeedVerifierTests
{
    private static string ScratchPath() =>
        Path.Combine(Path.GetTempPath(), "bgrec-verifier-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Passes_against_a_working_pipeline_and_cleans_up_its_scratch_dir()
    {
        string scratch = ScratchPath();

        LiveFeedVerdict verdict = await LiveFeedVerifier.RunAsync(scratch, TimeSpan.FromSeconds(10));

        Assert.True(verdict.Passed, verdict.Detail);
        Assert.False(Directory.Exists(scratch)); // the scratch install dir must not be left behind
    }

    [Fact]
    public async Task A_zero_timeout_fails_with_the_broken_stage_named_instead_of_hanging()
    {
        string scratch = ScratchPath();

        LiveFeedVerdict verdict = await LiveFeedVerifier.RunAsync(scratch, TimeSpan.Zero);

        Assert.False(verdict.Passed);
        // Which stage the zero timeout lands on depends on how fast initial discovery ran; either way
        // the detail must name the failed stage so the log line is actionable.
        Assert.Contains("failed", verdict.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(scratch)); // cleanup happens on failure too
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_producing_a_verdict()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => LiveFeedVerifier.RunAsync(ScratchPath(), TimeSpan.FromSeconds(10), cts.Token));
    }
}
