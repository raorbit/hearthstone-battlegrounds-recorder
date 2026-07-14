using BgRecorder.Core.Audio;
using BgRecorder.Core.Events;
using BgRecorder.Core.Session;
using Xunit;

namespace BgRecorder.Session.Tests;

/// <summary>
/// Adversarial-review micro-tests for the coordinator's command serialization: commands
/// arriving while a finalize is in flight, duplicate terminal events, and regressions for
/// the two defects found (and since fixed) in review — pause during the pre-resolve
/// buffering window, and capture-session disposal on finalize failure paths.
/// </summary>
public sealed class CoordinatorAdversarialTests
{
    // ------------------------------------------------- commands queued behind Finalizing

    [Fact]
    public async Task MatchStartsWhileFinalizing_IsQueuedAndBothMatchesRecord()
    {
        await using var h = new CoordinatorHarness();
        h.Muxer.Delay = TimeSpan.FromMilliseconds(300); // hold the loop inside FinalizeAsync
        await h.StartAsync();
        await h.StartMatchAsync();

        // MatchEnded starts the (slow) finalize; the next match's events are raised
        // immediately, so they are posted while the loop is still inside FinalizeAsync.
        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        h.Source.Raise(new MatchStarted(Ev.T0.AddMinutes(13)));
        h.Source.Raise(new GameTypeResolved(Ev.T0.AddMinutes(13).AddSeconds(1), BgGameType.Solo));

        await h.WaitUntilAsync(() => h.Recorder.StartCount == 2, what: "second recording to start");
        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(25), 1, PlayState.Won, Truncated: false));
        await h.WaitUntilAsync(() => h.Repository.Inserted.Count == 2, what: "both match rows inserted");
        await h.WaitForStateCountAsync(7);

        // Queued, not dropped, not corrupted: two full record cycles, staging clean.
        Assert.Equal(2, h.Muxer.Calls.Count);
        Assert.Empty(Directory.GetDirectories(h.StagingDir));
        Assert.Equal(
            new[]
            {
                CoordinatorState.Armed, CoordinatorState.Recording, CoordinatorState.Finalizing,
                CoordinatorState.Armed, CoordinatorState.Recording, CoordinatorState.Finalizing,
                CoordinatorState.Armed,
            },
            h.StateSequence());
        Assert.DoesNotContain(h.Diagnostics, d => d.Contains("Unhandled coordinator error"));
    }

    [Fact]
    public async Task PauseRequestedWhileFinalizing_EndsPausedAndNextMatchIsIgnored()
    {
        await using var h = new CoordinatorHarness();
        h.Muxer.Delay = TimeSpan.FromMilliseconds(300);
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        h.Coordinator.PauseAutoRecording(); // posted while the finalize runs
        await h.WaitForStateAsync(CoordinatorState.Paused);

        Assert.Single(h.Repository.Inserted); // the finalize itself completed normally

        h.Source.Raise(new MatchStarted(Ev.T0.AddMinutes(20)));
        h.Source.Raise(new GameTypeResolved(Ev.T0.AddMinutes(20).AddSeconds(1), BgGameType.Solo));
        await h.DrainAsync();
        Assert.Equal(1, h.Recorder.StartCount); // still paused: no new recording
        Assert.Equal(CoordinatorState.Paused, h.Coordinator.State);
    }

    [Fact]
    public async Task StopRequestedWhileFinalizing_CompletesWithoutASecondFinalize()
    {
        await using var h = new CoordinatorHarness();
        h.Muxer.Delay = TimeSpan.FromMilliseconds(300);
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        var stop = h.Coordinator.StopCurrentRecordingAsync(); // queued behind the finalize

        var done = await Task.WhenAny(stop, Task.Delay(5000));
        Assert.Same(stop, done); // must not hang
        Assert.Single(h.Repository.Inserted);
        Assert.Single(h.Muxer.Calls);
        Assert.Equal(CoordinatorState.Armed, h.Coordinator.State);
    }

    // ------------------------------------------------- duplicate / racing terminal inputs

    [Fact]
    public async Task DoubleMatchEnded_SecondIsANoOp()
    {
        await using var h = new CoordinatorHarness();
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        await h.DrainAsync();

        Assert.Single(h.Repository.Inserted);
        Assert.Single(h.Muxer.Calls);
        Assert.Equal(CoordinatorState.Armed, h.Coordinator.State);
        Assert.DoesNotContain(h.Diagnostics, d => d.Contains("Unhandled coordinator error"));
    }

    [Fact]
    public async Task LowSpaceAndMatchEndedRacing_SingleFinalize()
    {
        await using var h = new CoordinatorHarness();
        h.Muxer.Delay = TimeSpan.FromMilliseconds(200);
        await h.StartAsync();
        await h.StartMatchAsync();

        h.DiskSafety.LowSpaceCallback!(); // watchdog fires…
        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false)); // …as the match ends
        await h.DrainAsync();

        Assert.Single(h.Repository.Inserted);
        Assert.Single(h.Muxer.Calls);
        Assert.Equal(1, h.DiskSafety.WatchdogStartCount);
        Assert.Equal(1, h.DiskSafety.WatchdogDisposeCount);
        Assert.Equal(CoordinatorState.Armed, h.Coordinator.State);
        Assert.DoesNotContain(h.Diagnostics, d => d.Contains("Unhandled coordinator error"));
    }

    // ------------------------------------------------- fixed-defect regression tests

    /// <summary>
    /// Regression (fixed 2026-07-14): PauseAutoRecording issued between MatchStarted and
    /// GameTypeResolved used to set the state to Paused but leave _matchEvents buffered, so
    /// the subsequent GameTypeResolved started a recording anyway (Paused -> Recording) and
    /// the coordinator re-armed afterwards — the user's pause was silently discarded.
    /// HandlePause now discards the buffered match when it disarms.
    /// </summary>
    [Fact]
    public async Task PauseWhileMatchBuffered_DoesNotStartRecording_AndStaysPaused()
    {
        await using var h = new CoordinatorHarness();
        await h.StartAsync();
        await h.WaitForStateAsync(CoordinatorState.Armed);

        h.Source.Raise(new MatchStarted(Ev.T0)); // CREATE_GAME seen, game type not yet known
        await h.DrainAsync();                    // ensure the event was processed

        h.Coordinator.PauseAutoRecording();
        await h.WaitForStateAsync(CoordinatorState.Paused);

        h.Source.Raise(new GameTypeResolved(Ev.T0.AddSeconds(1), BgGameType.Solo));
        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        await h.DrainAsync();

        // Paused means disarmed: no recording may start, and the pause must survive.
        Assert.Equal(0, h.Recorder.StartCount);
        Assert.Equal(CoordinatorState.Paused, h.Coordinator.State);
    }

    /// <summary>
    /// Regression (fixed 2026-07-14): the DisposeQuietlyAsync calls used to sit inside the
    /// FinalizeAsync try, after MuxAndInsertAsync, so an exception escaping MuxAndInsertAsync
    /// (library path creation, FileInfo, or IMatchAssembler.Assemble) leaked both capture
    /// sessions and orphaned the already-muxed library file with no DB row. Disposal now
    /// lives in the finally, and MuxAndInsertAsync catches every step, dropping the orphan
    /// library file itself.
    /// </summary>
    [Fact]
    public async Task AssemblerFailureDuringFinalize_StillDisposesCaptureSessions()
    {
        await using var h = new CoordinatorHarness();
        h.Assembler.Throw = true;
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        await h.WaitForStateCountAsync(4);

        // Failure keeps the staged data…
        Assert.NotNull(h.SingleStagingSessionDir());
        Assert.Empty(h.Repository.Inserted);
        // …but the capture sessions must still be released…
        Assert.True(h.Recorder.LastSession!.Disposed, "video session leaked");
        Assert.True(h.Audio.LastSession!.Disposed, "audio session leaked");
        // …and the muxed-but-unregistered library file must not be left to become a
        // duplicate when StartupRecovery re-runs the finalize from staging.
        Assert.True(!Directory.Exists(h.LibraryDir) || Directory.GetFiles(h.LibraryDir).Length == 0);
    }

    /// <summary>
    /// Regression: an exception from an UNGUARDED finalize step (the watchdog Dispose, before any
    /// mux/insert) must still release both capture sessions via the finally. The AssemblerFailure
    /// test above cannot cover this because MuxAndInsertAsync swallows its own failures and returns
    /// false, so it never propagates into the finally — disposal there runs in the try either way.
    /// </summary>
    [Fact]
    public async Task UnguardedFinalizeStepThrows_StillDisposesCaptureSessions()
    {
        await using var h = new CoordinatorHarness();
        h.DiskSafety.WatchdogDisposeThrows = true; // ctx.Watchdog.Dispose() is the first, unguarded step
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        await h.WaitForStateCountAsync(4); // Armed, Recording, Finalizing, Armed

        // The finalize threw before muxing: nothing inserted, staging kept for recovery…
        Assert.Empty(h.Repository.Inserted);
        Assert.NotNull(h.SingleStagingSessionDir());
        // …but the finally must still release both native capture sessions.
        Assert.True(h.Recorder.LastSession!.Disposed, "video session leaked");
        Assert.True(h.Audio.LastSession!.Disposed, "audio session leaked");
        Assert.Contains(h.Diagnostics, d => d.Contains("Finalize failed"));
    }

    // ------------------------------------------------- idempotency & privacy surfacing

    /// <summary>
    /// A committed match carries the staging session id, so a crash-recovery re-run over the same
    /// staging folder is an idempotent no-op rather than a duplicate row/VOD.
    /// </summary>
    [Fact]
    public async Task FinalizedMatch_CarriesStagingSessionId()
    {
        await using var h = new CoordinatorHarness();
        await h.StartAsync();
        var sessionDir = default(string);
        await h.StartMatchAsync();
        sessionDir = h.SingleStagingSessionDir();
        Assert.NotNull(sessionDir);

        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        await h.WaitUntilAsync(() => h.Repository.Inserted.Count == 1, what: "match row inserted");

        var (match, _) = Assert.Single(h.Repository.Inserted);
        Assert.Equal(Path.GetFileName(sessionDir!), match.SessionId);
    }

    /// <summary>
    /// Privacy: when game-only audio is requested but the engine falls back to full system loopback,
    /// the coordinator must surface it so the user knows other apps' audio may be captured.
    /// </summary>
    [Fact]
    public async Task AudioDowngradedToSystemLoopback_IsSurfaced()
    {
        await using var h = new CoordinatorHarness();
        h.Audio.ForceActualMode = AudioCaptureMode.SystemLoopback; // engine had to broaden capture
        await h.StartAsync();
        await h.StartMatchAsync();

        Assert.Contains(h.Diagnostics, d => d.Contains("system audio", StringComparison.OrdinalIgnoreCase));
    }
}
