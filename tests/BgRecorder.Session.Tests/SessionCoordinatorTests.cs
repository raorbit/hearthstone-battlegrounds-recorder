using BgRecorder.Core.Data;
using BgRecorder.Core.Events;
using BgRecorder.Core.Session;
using BgRecorder.Session;
using Xunit;

namespace BgRecorder.Session.Tests;

public sealed class SessionCoordinatorTests
{
    // ---------------------------------------------------------------- happy path

    [Fact]
    public async Task FullMatch_RecordsMuxesInsertsAndCleansStaging()
    {
        await using var h = new CoordinatorHarness();
        h.Recorder.FirstFrameWallClock = Ev.T0.AddSeconds(2);
        h.Audio.FirstSampleWallClock = Ev.T0.AddSeconds(2.5);
        await h.StartAsync();
        await h.WaitForStateAsync(CoordinatorState.Armed);

        await h.StartMatchAsync();

        // Manifest lifecycle: while recording, an un-finalized manifest sits in the staging session.
        var sessionDir = h.SingleStagingSessionDir();
        Assert.NotNull(sessionDir);
        var manifest = ManifestStore.TryRead(sessionDir!);
        Assert.NotNull(manifest);
        Assert.False(manifest!.FinalizedCleanly);
        Assert.Equal(Ev.T0, manifest.StartedAt);

        foreach (var e in Ev.FullMatch().Skip(2))
        {
            h.Source.Raise(e);
        }
        await h.WaitForStateCountAsync(4);

        Assert.Equal(
            new[] { CoordinatorState.Armed, CoordinatorState.Recording, CoordinatorState.Finalizing, CoordinatorState.Armed },
            h.StateSequence());

        // Muxed with the staged audio at the A/V clock offset.
        var call = Assert.Single(h.Muxer.Calls);
        Assert.EndsWith("video.mp4", call.Video);
        Assert.EndsWith("audio.wav", call.Audio);
        Assert.Equal(TimeSpan.FromSeconds(0.5), call.Offset);
        Assert.True(File.Exists(call.Output));
        Assert.StartsWith(h.LibraryDir, call.Output);

        // Assembled as Complete from the full buffered event list.
        var assembled = Assert.Single(h.Assembler.Calls);
        Assert.Equal(VideoStatus.Complete, assembled.Status);
        Assert.Equal(Ev.FullMatch().Length, assembled.Events.Count);
        Assert.IsType<MatchStarted>(assembled.Events[0]);
        Assert.IsType<MatchEnded>(assembled.Events[^1]);
        Assert.NotNull(assembled.Timeline);
        Assert.Equal(Ev.T0.AddSeconds(2), assembled.Timeline!.VideoFirstFrameWallClock);
        Assert.Equal(call.Output, assembled.Timeline.FinalVideoPath);

        // Row inserted; staging session fully cleaned up; watchdog ran and was disposed.
        Assert.Single(h.Repository.Inserted);
        Assert.Empty(Directory.GetDirectories(h.StagingDir));
        Assert.Equal(1, h.DiskSafety.WatchdogStartCount);
        Assert.Equal(1, h.DiskSafety.WatchdogDisposeCount);
        Assert.Equal(1, h.Recorder.StartCount);
        Assert.Equal(1, h.Audio.StartCount);
    }

    [Fact]
    public async Task Manifest_AccumulatesEventsWhileRecording()
    {
        await using var h = new CoordinatorHarness();
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Source.Raise(new LocalHeroResolved(Ev.T0.AddSeconds(5), "BG31_HERO_001"));
        h.Source.Raise(new TurnStarted(Ev.T0.AddSeconds(10), 1, 1));

        var sessionDir = h.SingleStagingSessionDir()!;
        // Writes are throttled to >=1s apart but deferred, never dropped: the events must land.
        await h.WaitUntilAsync(
            () => ManifestStore.TryRead(sessionDir)?.Events.Count >= 4,
            timeoutMs: 5000,
            what: "manifest to contain the buffered events");

        var manifest = ManifestStore.TryRead(sessionDir)!;
        Assert.False(manifest.FinalizedCleanly);
        Assert.Collection(manifest.Events,
            e => Assert.IsType<MatchStarted>(e),
            e => Assert.IsType<GameTypeResolved>(e),
            e => Assert.IsType<LocalHeroResolved>(e),
            e => Assert.IsType<TurnStarted>(e));
    }

    // ---------------------------------------------------------------- stop / pause / resume

    [Fact]
    public async Task EarlyManualStop_FinalizesAndStaysArmed()
    {
        await using var h = new CoordinatorHarness();
        await h.StartAsync();
        await h.StartMatchAsync();

        await h.Coordinator.StopCurrentRecordingAsync();

        Assert.Equal(CoordinatorState.Armed, h.Coordinator.State);
        Assert.Equal(
            new[] { CoordinatorState.Armed, CoordinatorState.Recording, CoordinatorState.Finalizing, CoordinatorState.Armed },
            h.StateSequence());
        Assert.Single(h.Repository.Inserted);
        Assert.Single(h.Muxer.Calls);
        Assert.Empty(Directory.GetDirectories(h.StagingDir));
    }

    [Fact]
    public async Task PauseDuringRecording_FinishesTheMatchThenPauses()
    {
        await using var h = new CoordinatorHarness();
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Coordinator.PauseAutoRecording();
        await Task.Delay(100); // pause must not interrupt the running recording
        Assert.Equal(CoordinatorState.Recording, h.Coordinator.State);

        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        await h.WaitForStateAsync(CoordinatorState.Paused);

        Assert.Equal(
            new[] { CoordinatorState.Armed, CoordinatorState.Recording, CoordinatorState.Finalizing, CoordinatorState.Paused },
            h.StateSequence());
        Assert.Single(h.Repository.Inserted); // the recording still finished normally

        // A match starting while paused is ignored entirely.
        h.Source.Raise(new MatchStarted(Ev.T0.AddMinutes(20)));
        h.Source.Raise(new GameTypeResolved(Ev.T0.AddMinutes(20).AddSeconds(1), BgGameType.Solo));
        await h.DrainAsync();
        Assert.Equal(1, h.Recorder.StartCount);
        Assert.Equal(CoordinatorState.Paused, h.Coordinator.State);
    }

    [Fact]
    public async Task ResumeNow_ReArms()
    {
        await using var h = new CoordinatorHarness();
        await h.StartAsync();
        await h.WaitForStateAsync(CoordinatorState.Armed);

        h.Coordinator.PauseAutoRecording();
        await h.WaitForStateAsync(CoordinatorState.Paused);

        h.Coordinator.ResumeNow();
        await h.WaitUntilAsync(() => h.Coordinator.State == CoordinatorState.Armed, what: "re-arm after ResumeNow");

        Assert.Equal(
            new[] { CoordinatorState.Armed, CoordinatorState.Paused, CoordinatorState.Armed },
            h.StateSequence());
    }

    [Fact]
    public async Task ResumeNow_DuringRecording_CancelsAPendingPause()
    {
        await using var h = new CoordinatorHarness();
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Coordinator.PauseAutoRecording();
        h.Coordinator.ResumeNow();
        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        await h.WaitForStateCountAsync(4);

        Assert.Equal(CoordinatorState.Armed, h.Coordinator.State); // not Paused
    }

    // ---------------------------------------------------------------- non-BG and refusals

    [Fact]
    public async Task NotBattlegrounds_IgnoredEntirely()
    {
        await using var h = new CoordinatorHarness();
        await h.StartAsync();
        await h.WaitForStateAsync(CoordinatorState.Armed);

        h.Source.Raise(new MatchStarted(Ev.T0));
        h.Source.Raise(new GameTypeResolved(Ev.T0.AddSeconds(1), BgGameType.NotBattlegrounds));
        h.Source.Raise(new TurnStarted(Ev.T0.AddSeconds(10), 1, 1));
        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(9), null, PlayState.Won, Truncated: false));
        await h.DrainAsync();

        Assert.Equal(0, h.Recorder.StartCount);
        Assert.Equal(0, h.Audio.StartCount);
        Assert.Empty(h.Repository.Inserted);
        Assert.Equal(new[] { CoordinatorState.Armed }, h.StateSequence()); // never left Armed
        Assert.False(Directory.Exists(h.StagingDir)); // no staging session was even created

        // …and the next real BG match still records.
        await h.StartMatchAsync();
        Assert.Equal(1, h.Recorder.StartCount);
    }

    [Fact]
    public async Task DiskFloorRefusal_ArmsNothingAndSurfacesReason()
    {
        await using var h = new CoordinatorHarness();
        h.DiskSafety.ArmResult = new ArmCheckResult(false, "Free space 3.2 GB is below the 10 GB safety floor");
        await h.StartAsync();
        await h.WaitForStateAsync(CoordinatorState.Armed);

        h.Source.Raise(new MatchStarted(Ev.T0));
        h.Source.Raise(new GameTypeResolved(Ev.T0.AddSeconds(1), BgGameType.Solo));
        await h.DrainAsync();

        Assert.Equal(0, h.Recorder.StartCount);
        Assert.Equal(CoordinatorState.Armed, h.Coordinator.State);
        Assert.Equal(new[] { CoordinatorState.Armed }, h.StateSequence());
        Assert.Contains(h.Diagnostics, d => d.Contains("below the 10 GB safety floor"));
        Assert.False(Directory.Exists(h.StagingDir));
    }

    [Fact]
    public async Task RecorderStartFailure_StaysArmedAndCleansTheEmptySession()
    {
        await using var h = new CoordinatorHarness();
        h.Recorder.ThrowOnStart = true;
        await h.StartAsync();
        await h.WaitForStateAsync(CoordinatorState.Armed);

        h.Source.Raise(new MatchStarted(Ev.T0));
        h.Source.Raise(new GameTypeResolved(Ev.T0.AddSeconds(1), BgGameType.Solo));
        await h.DrainAsync();

        Assert.Equal(CoordinatorState.Armed, h.Coordinator.State);
        Assert.Equal(new[] { CoordinatorState.Armed }, h.StateSequence());
        Assert.Empty(Directory.GetDirectories(h.StagingDir)); // the just-created session dir was removed
        Assert.Contains(h.Diagnostics, d => d.Contains("Could not start video capture"));
    }

    // ---------------------------------------------------------------- watchdog

    [Fact]
    public async Task WatchdogLowSpace_FinalizesEarly_RowInsertedComplete()
    {
        await using var h = new CoordinatorHarness();
        await h.StartAsync();
        await h.StartMatchAsync();

        h.DiskSafety.LowSpaceCallback!();
        await h.WaitForStateCountAsync(4);

        Assert.Equal(
            new[] { CoordinatorState.Armed, CoordinatorState.Recording, CoordinatorState.Finalizing, CoordinatorState.Armed },
            h.StateSequence());
        var (match, _) = Assert.Single(h.Repository.Inserted);
        Assert.Equal(VideoStatus.Complete, match.VideoStatus);
        Assert.Single(h.Muxer.Calls);
        Assert.Empty(Directory.GetDirectories(h.StagingDir));
        Assert.Contains(h.Diagnostics, d => d.Contains("Low disk space"));
    }

    // ---------------------------------------------------------------- audio failure tolerance

    [Fact]
    public async Task AudioStartFailure_StillRecordsAndMuxesVideoOnly()
    {
        await using var h = new CoordinatorHarness();
        h.Audio.ThrowOnStart = true;
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        await h.WaitForStateCountAsync(4);

        var call = Assert.Single(h.Muxer.Calls);
        Assert.Equal(string.Empty, call.Audio); // video-only mux
        Assert.Single(h.Repository.Inserted);
        Assert.Empty(Directory.GetDirectories(h.StagingDir));
        Assert.Contains(h.Diagnostics, d => d.Contains("Audio capture unavailable"));
    }

    [Fact]
    public async Task AudioStopFailure_DoesNotAbortVideoFinalize()
    {
        await using var h = new CoordinatorHarness();
        h.Audio.StopThrows = true;
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        await h.WaitForStateCountAsync(4);

        var call = Assert.Single(h.Muxer.Calls);
        Assert.Equal(string.Empty, call.Audio);
        Assert.Single(h.Repository.Inserted);
        Assert.Empty(Directory.GetDirectories(h.StagingDir));
    }

    [Fact]
    public async Task AudioFailedEventMidRecording_VideoFinalizesWithoutAudio()
    {
        await using var h = new CoordinatorHarness();
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Audio.LastSession!.RaiseFailed("device lost");
        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        await h.WaitForStateCountAsync(4);

        var call = Assert.Single(h.Muxer.Calls);
        Assert.Equal(string.Empty, call.Audio);
        Assert.False(h.Audio.LastSession!.Stopped); // dead session is not asked to stop
        Assert.Single(h.Repository.Inserted);
        Assert.Contains(h.Diagnostics, d => d.Contains("device lost"));
    }

    /// <summary>
    /// Regression (fixed 2026-07-14): a staged audio WAV the muxer rejects (e.g. crash-corrupted)
    /// used to fail the finalize outright with no video-only fallback, stranding the video in
    /// staging forever. The mux must retry once video-only and the match must still register.
    /// </summary>
    [Fact]
    public async Task UnreadableStagedAudio_FallsBackToVideoOnlyMux()
    {
        await using var h = new CoordinatorHarness();
        h.Muxer.ThrowOnAudio = true;
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        await h.WaitForStateCountAsync(4);

        // First attempt carried the audio; the retry was video-only to the same output.
        Assert.Equal(2, h.Muxer.Calls.Count);
        Assert.EndsWith("audio.wav", h.Muxer.Calls[0].Audio);
        Assert.Equal(string.Empty, h.Muxer.Calls[1].Audio);
        Assert.Equal(h.Muxer.Calls[0].Output, h.Muxer.Calls[1].Output);
        Assert.True(File.Exists(h.Muxer.Calls[1].Output));

        var (match, _) = Assert.Single(h.Repository.Inserted);
        Assert.Equal(VideoStatus.Complete, match.VideoStatus);
        Assert.Empty(Directory.GetDirectories(h.StagingDir)); // finalize completed; staging cleaned
        Assert.Equal(CoordinatorState.Armed, h.Coordinator.State);
        Assert.Contains(h.Diagnostics, d => d.Contains("retrying video-only"));
    }

    // ---------------------------------------------------------------- truncated match

    [Fact]
    public async Task TruncatedMatchEnded_StillFinalizes()
    {
        await using var h = new CoordinatorHarness();
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(5), null, PlayState.Unknown, Truncated: true));
        await h.WaitForStateCountAsync(4);

        var (match, _) = Assert.Single(h.Repository.Inserted);
        Assert.True(match.Truncated);
        Assert.Single(h.Muxer.Calls);
        Assert.Empty(Directory.GetDirectories(h.StagingDir));
        Assert.Equal(CoordinatorState.Armed, h.Coordinator.State);
    }

    // ---------------------------------------------------------------- failure keeps data

    [Fact]
    public async Task RepositoryInsertFailure_KeepsStagingForRecovery()
    {
        await using var h = new CoordinatorHarness();
        h.Repository.ThrowOnInsert = true;
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        await h.WaitForStateCountAsync(4);

        // Nothing lost: the staged session (video + manifest without FinalizedCleanly) survives.
        var sessionDir = h.SingleStagingSessionDir();
        Assert.NotNull(sessionDir);
        Assert.True(File.Exists(Path.Combine(sessionDir!, "video.mp4")));
        var manifest = ManifestStore.TryRead(sessionDir!);
        Assert.NotNull(manifest);
        Assert.False(manifest!.FinalizedCleanly);
        Assert.Equal(3, manifest.Events.Count); // MatchStarted, GameTypeResolved, MatchEnded persisted

        // The unreferenced library file was dropped so recovery is the single re-entry path.
        Assert.True(!Directory.Exists(h.LibraryDir) || Directory.GetFiles(h.LibraryDir).Length == 0);
        Assert.Equal(CoordinatorState.Armed, h.Coordinator.State);
        Assert.Contains(h.Diagnostics, d => d.Contains("Could not save the match row"));
    }

    [Fact]
    public async Task MuxFailure_KeepsStagingForRecovery()
    {
        await using var h = new CoordinatorHarness();
        h.Muxer.Throw = true;
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        await h.WaitForStateCountAsync(4);

        Assert.NotNull(h.SingleStagingSessionDir());
        Assert.Empty(h.Repository.Inserted);
        Assert.Equal(CoordinatorState.Armed, h.Coordinator.State);
        Assert.Contains(h.Diagnostics, d => d.Contains("Mux failed"));
    }

    [Fact]
    public async Task VideoFailedEvent_FinalizesAndLeavesStagingWhenStopAlsoFails()
    {
        await using var h = new CoordinatorHarness();
        h.Recorder.StopThrows = true;
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Recorder.LastSession!.RaiseFailed("encoder died");
        await h.WaitForStateCountAsync(4);

        // No RecordingResult means no mux/insert now; the staged fragmented MP4 stays for recovery.
        Assert.Empty(h.Muxer.Calls);
        Assert.Empty(h.Repository.Inserted);
        var sessionDir = h.SingleStagingSessionDir();
        Assert.NotNull(sessionDir);
        Assert.True(File.Exists(Path.Combine(sessionDir!, "video.mp4")));
        Assert.Equal(CoordinatorState.Armed, h.Coordinator.State);
    }

    // ---------------------------------------------------------------- source gone

    [Fact]
    public async Task GameGoneAfterFinalize_TransitionsToGameNotFound()
    {
        await using var h = new CoordinatorHarness();
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Locator.Target = null; // the game exited during the match
        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: true));
        await h.WaitForStateAsync(CoordinatorState.GameNotFound);

        Assert.Equal(
            new[] { CoordinatorState.Armed, CoordinatorState.Recording, CoordinatorState.Finalizing, CoordinatorState.GameNotFound },
            h.StateSequence());
        Assert.Single(h.Repository.Inserted); // the match itself still finalized

        // A new log session (game restarted) re-arms.
        h.Locator.Target = new BgRecorder.Core.Capture.RecordingTarget(9999, "Hearthstone");
        h.Source.Raise(new LogSessionChanged(Ev.T0.AddMinutes(15), @"C:\logs\new"));
        await h.WaitUntilAsync(() => h.Coordinator.State == CoordinatorState.Armed, what: "re-arm on new log session");
    }
}
