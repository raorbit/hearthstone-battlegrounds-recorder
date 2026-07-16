using BgRecorder.Core;
using BgRecorder.Core.Data;
using BgRecorder.Core.Events;
using BgRecorder.Core.Session;
using BgRecorder.Session;
using Xunit;

namespace BgRecorder.Session.Tests;

public sealed class StartupRecoveryTests : IDisposable
{
    private readonly string _root;
    private readonly AppSettings _settings;
    private readonly FakeMuxer _muxer = new();
    private readonly FakeThumbnailExtractor _thumbnailer = new();
    private readonly FakeAssembler _assembler = new();
    private readonly FakeRepository _repository = new();

    public StartupRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "bgrec-recovery-tests", Guid.NewGuid().ToString("N"));
        _settings = new AppSettings
        {
            StagingDir = Path.Combine(_root, "staging"),
            LibraryDir = Path.Combine(_root, "library"),
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // best effort
        }
    }

    private StartupRecovery CreateSut() => new(_muxer, _thumbnailer, _assembler, _repository, _settings);

    private string CreateSessionDir(out string videoPath, out string audioPath, bool withVideo = true, bool withAudio = true)
    {
        var dir = Path.Combine(_settings.StagingDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        videoPath = Path.Combine(dir, "video.mp4");
        audioPath = Path.Combine(dir, "audio.wav");
        if (withVideo)
        {
            File.WriteAllBytes(videoPath, new byte[64]);
        }
        if (withAudio)
        {
            File.WriteAllBytes(audioPath, new byte[32]);
        }
        return dir;
    }

    private static StagingManifest Manifest(string videoPath, string audioPath, bool finalized = false,
        DateTimeOffset? videoClock = null, DateTimeOffset? audioClock = null, IReadOnlyList<GameEvent>? events = null)
        => new()
        {
            SessionId = "s1",
            StartedAt = Ev.T0,
            VideoPath = videoPath,
            AudioPath = audioPath,
            VideoFirstFrameWallClock = videoClock,
            AudioFirstSampleWallClock = audioClock,
            Events = events ?? Ev.FullMatch(),
            FinalizedCleanly = finalized,
        };

    // ---------------------------------------------------------------- recovery matrix

    [Fact]
    public async Task OrphanWithBothFiles_MuxedInsertedIncomplete_StagingDeleted()
    {
        var dir = CreateSessionDir(out var video, out var audio);
        ManifestStore.Write(dir, Manifest(video, audio,
            videoClock: Ev.T0.AddSeconds(2), audioClock: Ev.T0.AddSeconds(3)));

        var report = await CreateSut().RunAsync();

        var result = Assert.Single(report.Sessions);
        Assert.Equal(RecoveryOutcome.Recovered, result.Outcome);

        var call = Assert.Single(_muxer.Calls);
        Assert.Equal(video, call.Video);
        Assert.Equal(audio, call.Audio);
        Assert.Equal(TimeSpan.FromSeconds(1), call.Offset); // audio clock - video clock
        Assert.True(File.Exists(call.Output));

        var assembled = Assert.Single(_assembler.Calls);
        Assert.Equal(VideoStatus.Incomplete, assembled.Status);
        Assert.NotNull(assembled.Timeline);
        Assert.Equal(Ev.T0.AddSeconds(2), assembled.Timeline!.VideoFirstFrameWallClock);
        Assert.Equal(Ev.FullMatch().Length, assembled.Events.Count); // events came from the manifest

        var (match, _) = Assert.Single(_repository.Inserted);
        Assert.Equal(VideoStatus.Incomplete, match.VideoStatus);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task OrphanVideoOnly_MuxedWithoutAudio()
    {
        var dir = CreateSessionDir(out var video, out var audio, withAudio: false);
        ManifestStore.Write(dir, Manifest(video, audio, videoClock: Ev.T0.AddSeconds(2)));

        var report = await CreateSut().RunAsync();

        Assert.Equal(RecoveryOutcome.Recovered, Assert.Single(report.Sessions).Outcome);
        var call = Assert.Single(_muxer.Calls);
        Assert.Equal(string.Empty, call.Audio);
        Assert.Single(_repository.Inserted);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task OrphanWithMissingClocks_RegisteredWithEmptyMarkers()
    {
        var dir = CreateSessionDir(out var video, out var audio);
        ManifestStore.Write(dir, Manifest(video, audio)); // no clocks were ever learned

        var report = await CreateSut().RunAsync();

        Assert.Equal(RecoveryOutcome.Recovered, Assert.Single(report.Sessions).Outcome);
        Assert.Equal(TimeSpan.Zero, Assert.Single(_muxer.Calls).Offset);

        // No video clock -> assembler saw no timeline, and the inserted row still references the file.
        var assembled = Assert.Single(_assembler.Calls);
        Assert.Null(assembled.Timeline);
        var (match, markers) = Assert.Single(_repository.Inserted);
        Assert.Empty(markers);
        Assert.Equal(VideoStatus.Incomplete, match.VideoStatus);
        Assert.NotNull(match.VideoPath);
        Assert.True(File.Exists(match.VideoPath));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task CorruptManifestWithVideo_StillRegisteredIncomplete()
    {
        var dir = CreateSessionDir(out var video, out _);
        File.WriteAllText(ManifestStore.PathFor(dir), "{ this is not json");

        var report = await CreateSut().RunAsync();

        Assert.Equal(RecoveryOutcome.Recovered, Assert.Single(report.Sessions).Outcome);
        var call = Assert.Single(_muxer.Calls);
        Assert.Equal(video, call.Video);
        Assert.Equal(TimeSpan.Zero, call.Offset);

        Assert.Empty(_assembler.Calls); // no readable events: the minimal row is built directly
        var (match, markers) = Assert.Single(_repository.Inserted);
        Assert.Equal(VideoStatus.Incomplete, match.VideoStatus);
        Assert.True(match.Truncated);
        Assert.Empty(markers);
        Assert.NotNull(match.VideoPath);
        Assert.True(File.Exists(match.VideoPath));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task AlreadyFinalizedManifest_StagedDuplicateReclaimed()
    {
        var dir = CreateSessionDir(out var video, out var audio);
        ManifestStore.Write(dir, Manifest(video, audio, finalized: true));

        var report = await CreateSut().RunAsync();

        Assert.Equal(RecoveryOutcome.SkippedFinalized, Assert.Single(report.Sessions).Outcome);
        Assert.Empty(_muxer.Calls);          // never re-registered
        Assert.Empty(_repository.Inserted);
        // The row + library file already exist, so the staged copy is a pure duplicate: reclaimed,
        // not leaked forever. (Idempotent inserts make reclaiming safe.)
        Assert.False(Directory.Exists(dir));
    }

    /// <summary>
    /// Idempotency: a crash between the DB commit and the staging delete (or a delete that failed)
    /// leaves an unfinalized folder whose match is already recorded. Recovery must NOT re-mux/insert
    /// a duplicate — it recognises the session id and just reclaims the staged copy.
    /// </summary>
    [Fact]
    public async Task OrphanWhoseMatchAlreadyRecorded_ReclaimedWithoutDuplicating()
    {
        var dir = CreateSessionDir(out var video, out var audio);
        ManifestStore.Write(dir, Manifest(video, audio, videoClock: Ev.T0.AddSeconds(2)));
        // Folder name is the session id; mark it already-recorded (as a prior finalize would have).
        _repository.ExistingSessions.Add(Path.GetFileName(dir));

        var report = await CreateSut().RunAsync();

        Assert.Equal(RecoveryOutcome.AlreadyRecorded, Assert.Single(report.Sessions).Outcome);
        Assert.Empty(_muxer.Calls);          // no re-mux
        Assert.Empty(_repository.Inserted);  // no duplicate row
        Assert.False(Directory.Exists(dir)); // staged duplicate reclaimed
    }

    /// <summary>The recovered match carries the staging session id so a re-run stays idempotent.</summary>
    [Fact]
    public async Task RecoveredMatch_CarriesSessionId()
    {
        var dir = CreateSessionDir(out var video, out var audio);
        ManifestStore.Write(dir, Manifest(video, audio, videoClock: Ev.T0.AddSeconds(2)));

        await CreateSut().RunAsync();

        var (match, _) = Assert.Single(_repository.Inserted);
        Assert.Equal(Path.GetFileName(dir), match.SessionId);
    }

    // ---------------------------------------------------------------- poison staged audio

    /// <summary>
    /// Regression (fixed 2026-07-14): a present-but-unreadable staged WAV (0-byte, truncated
    /// header, garbage — what a hard kill can leave) made every recovery mux re-throw forever,
    /// permanently stranding the video. Recovery must retry video-only and register the match.
    /// </summary>
    [Fact]
    public async Task UnreadableStagedAudio_OrphanStillRecoveredVideoOnly()
    {
        var dir = CreateSessionDir(out var video, out var audio);
        ManifestStore.Write(dir, Manifest(video, audio,
            videoClock: Ev.T0.AddSeconds(2), audioClock: Ev.T0.AddSeconds(3)));
        _muxer.ThrowOnAudio = true;

        var report = await CreateSut().RunAsync();

        var result = Assert.Single(report.Sessions);
        Assert.Equal(RecoveryOutcome.Recovered, result.Outcome);
        Assert.Contains("video-only", result.Detail);

        Assert.Equal(2, _muxer.Calls.Count);
        Assert.Equal(audio, _muxer.Calls[0].Audio);
        Assert.Equal(string.Empty, _muxer.Calls[1].Audio);
        Assert.Equal(_muxer.Calls[0].Output, _muxer.Calls[1].Output);
        Assert.True(File.Exists(_muxer.Calls[1].Output));

        var (match, _) = Assert.Single(_repository.Inserted);
        Assert.Equal(VideoStatus.Incomplete, match.VideoStatus);
        Assert.False(Directory.Exists(dir)); // no poison folder left to re-fail next startup
    }

    [Fact]
    public async Task UnreadableStagedAudio_WithoutManifest_StillRecoveredVideoOnly()
    {
        var dir = CreateSessionDir(out var video, out var audio); // no manifest written at all
        _muxer.ThrowOnAudio = true;

        var report = await CreateSut().RunAsync();

        var result = Assert.Single(report.Sessions);
        Assert.Equal(RecoveryOutcome.Recovered, result.Outcome);
        Assert.Equal(2, _muxer.Calls.Count);
        Assert.Equal(audio, _muxer.Calls[0].Audio);
        Assert.Equal(string.Empty, _muxer.Calls[1].Audio);

        var (match, _) = Assert.Single(_repository.Inserted);
        Assert.Equal(VideoStatus.Incomplete, match.VideoStatus);
        Assert.True(File.Exists(match.VideoPath));
        Assert.False(Directory.Exists(dir));
    }

    // ---------------------------------------------------------------- failure keeps the video

    [Fact]
    public async Task MuxFailure_NeverDeletesTheStagedVideo()
    {
        var dir = CreateSessionDir(out var video, out var audio);
        ManifestStore.Write(dir, Manifest(video, audio, videoClock: Ev.T0.AddSeconds(2)));
        _muxer.Throw = true;

        var report = await CreateSut().RunAsync();

        var result = Assert.Single(report.Sessions);
        Assert.Equal(RecoveryOutcome.LeftInPlace, result.Outcome);
        Assert.Contains("preserved", result.Detail);
        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(video));
        Assert.Empty(_repository.Inserted);
    }

    [Fact]
    public async Task InsertFailure_KeepsStagingAndDropsTheOrphanLibraryFile()
    {
        var dir = CreateSessionDir(out var video, out var audio);
        ManifestStore.Write(dir, Manifest(video, audio, videoClock: Ev.T0.AddSeconds(2)));
        _repository.ThrowOnInsert = true;

        var report = await CreateSut().RunAsync();

        Assert.Equal(RecoveryOutcome.LeftInPlace, Assert.Single(report.Sessions).Outcome);
        Assert.True(File.Exists(video));
        Assert.True(!Directory.Exists(_settings.LibraryDir) || Directory.GetFiles(_settings.LibraryDir).Length == 0);
    }

    [Fact]
    public async Task RecoveredMatch_WithSuccessfulThumbnail_StampsThumbnailPath()
    {
        _thumbnailer.Succeed = true; // decode a thumbnail from the recovered library MP4
        var dir = CreateSessionDir(out var video, out var audio);
        ManifestStore.Write(dir, Manifest(video, audio, videoClock: Ev.T0.AddSeconds(2)));

        await CreateSut().RunAsync();

        var (match, _) = Assert.Single(_repository.Inserted);
        Assert.False(string.IsNullOrEmpty(match.ThumbnailPath));
        Assert.Single(_thumbnailer.Calls);
    }

    [Fact]
    public async Task InsertFailure_WithThumbnail_DeletesTheOrphanThumbnailToo()
    {
        _thumbnailer.Succeed = true; // a thumbnail .bmp is written before the (failing) insert
        var dir = CreateSessionDir(out var video, out var audio);
        ManifestStore.Write(dir, Manifest(video, audio, videoClock: Ev.T0.AddSeconds(2)));
        _repository.ThrowOnInsert = true;

        var report = await CreateSut().RunAsync();

        Assert.Equal(RecoveryOutcome.LeftInPlace, Assert.Single(report.Sessions).Outcome);
        Assert.True(File.Exists(video)); // staged video preserved for a retry
        // Neither the muxed library MP4 nor its thumbnail sibling is left orphaned in the library.
        Assert.True(!Directory.Exists(_settings.LibraryDir) || Directory.GetFiles(_settings.LibraryDir).Length == 0);
    }

    /// <summary>
    /// Conflict-safe recovery: if the insert fails because another writer already committed this
    /// session's row (a uniqueness conflict), that row references the SAME deterministic output path,
    /// so recovery must NOT delete the muxed library file — it reclaims the staged duplicate instead.
    /// </summary>
    [Fact]
    public async Task InsertConflict_WhenRowAlreadyCommitted_KeepsLibraryFileAndReclaimsStaging()
    {
        var dir = CreateSessionDir(out var video, out var audio);
        ManifestStore.Write(dir, Manifest(video, audio, videoClock: Ev.T0.AddSeconds(2)));
        _repository.ThrowOnInsert = true;
        _repository.MarkSessionRecordedOnInsertThrow = true;

        var report = await CreateSut().RunAsync();

        Assert.Equal(RecoveryOutcome.AlreadyRecorded, Assert.Single(report.Sessions).Outcome);
        var output = Assert.Single(_muxer.Calls).Output;
        Assert.True(File.Exists(output), "the committed row's library file must not be deleted");
        Assert.False(Directory.Exists(dir), "staged duplicate reclaimed");
    }

    // ---------------------------------------------------------------- degenerate folders

    [Fact]
    public async Task OrphanManifestWithoutVideo_FolderRemoved()
    {
        var dir = CreateSessionDir(out var video, out var audio, withVideo: false, withAudio: false);
        ManifestStore.Write(dir, Manifest(video, audio));

        var report = await CreateSut().RunAsync();

        Assert.Equal(RecoveryOutcome.NothingToRecover, Assert.Single(report.Sessions).Outcome);
        Assert.False(Directory.Exists(dir));
        Assert.Empty(_repository.Inserted);
    }

    [Fact]
    public async Task EmptyFolderWithoutManifest_FolderRemoved()
    {
        var dir = Path.Combine(_settings.StagingDir, "empty");
        Directory.CreateDirectory(dir);

        var report = await CreateSut().RunAsync();

        Assert.Equal(RecoveryOutcome.NothingToRecover, Assert.Single(report.Sessions).Outcome);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task MissingStagingRoot_ReportsNothing()
    {
        var report = await CreateSut().RunAsync();
        Assert.Empty(report.Sessions);
    }

    [Fact]
    public async Task MixedSessions_EachHandledIndependently()
    {
        var okDir = CreateSessionDir(out var okVideo, out var okAudio);
        ManifestStore.Write(okDir, Manifest(okVideo, okAudio, videoClock: Ev.T0.AddSeconds(2)));
        var doneDir = CreateSessionDir(out var doneVideo, out var doneAudio);
        ManifestStore.Write(doneDir, Manifest(doneVideo, doneAudio, finalized: true));

        var report = await CreateSut().RunAsync();

        Assert.Equal(2, report.Sessions.Count);
        Assert.Contains(report.Sessions, s => s.SessionDir == okDir && s.Outcome == RecoveryOutcome.Recovered);
        Assert.Contains(report.Sessions, s => s.SessionDir == doneDir && s.Outcome == RecoveryOutcome.SkippedFinalized);
        Assert.False(Directory.Exists(okDir));
        Assert.False(Directory.Exists(doneDir)); // finalized staging duplicate reclaimed
    }
}
