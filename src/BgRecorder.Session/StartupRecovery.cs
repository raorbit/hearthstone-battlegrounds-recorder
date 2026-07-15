using BgRecorder.Core;
using BgRecorder.Core.Audio;
using BgRecorder.Core.Data;
using BgRecorder.Core.Events;
using BgRecorder.Core.Session;

namespace BgRecorder.Session;

/// <summary>
/// Startup pass over the staging root. A session folder whose manifest lacks
/// FinalizedCleanly means the process died mid-recording: mux whatever media exists,
/// register the match as <see cref="VideoStatus.Incomplete"/> from the manifest's
/// persisted events, and only then delete the staging folder. A staged video that
/// fails to recover is never deleted — it stays in place and is reported.
/// </summary>
public sealed class StartupRecovery
{
    private readonly IMuxer _muxer;
    private readonly IMatchAssembler _assembler;
    private readonly IMatchRepository _repository;
    private readonly AppSettings _settings;

    public StartupRecovery(IMuxer muxer, IMatchAssembler assembler, IMatchRepository repository, AppSettings settings)
    {
        _muxer = muxer;
        _assembler = assembler;
        _repository = repository;
        _settings = settings;
    }

    public async Task<RecoveryReport> RunAsync(CancellationToken ct = default)
    {
        var results = new List<RecoverySessionResult>();
        if (!Directory.Exists(_settings.StagingDir))
        {
            return new RecoveryReport(results);
        }
        foreach (var sessionDir in Directory.GetDirectories(_settings.StagingDir))
        {
            ct.ThrowIfCancellationRequested();
            RecoverySessionResult result;
            try
            {
                result = await RecoverSessionAsync(sessionDir, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new RecoverySessionResult(sessionDir, RecoveryOutcome.LeftInPlace, $"Recovery failed: {ex.Message}");
            }
            results.Add(result);
        }
        return new RecoveryReport(results);
    }

    private async Task<RecoverySessionResult> RecoverSessionAsync(string sessionDir, CancellationToken ct)
    {
        var manifest = ManifestStore.TryRead(sessionDir);
        // The staging folder name IS the recording's stable session id (StagingDir/<sessionId>),
        // available even when the manifest is unreadable.
        var sessionId = Path.GetFileName(sessionDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (manifest is { FinalizedCleanly: true })
        {
            // Clean finalize whose folder delete didn't happen; the row and library file already
            // exist, so the staged files are a pure duplicate. Reclaim them — safe now that inserts
            // are idempotent on SessionId — instead of leaking a full staging copy forever.
            TryDelete(sessionDir);
            return new RecoverySessionResult(sessionDir, RecoveryOutcome.SkippedFinalized,
                "Already finalized; staged duplicate reclaimed.");
        }

        // Idempotency: if this session's row was already committed (a crash between the DB commit
        // and the staging delete, or a delete that failed), do not re-mux/re-insert a duplicate —
        // just reclaim the staged copy.
        if (!string.IsNullOrEmpty(sessionId)
            && await _repository.MatchExistsBySessionAsync(sessionId, ct).ConfigureAwait(false))
        {
            TryDelete(sessionDir);
            return new RecoverySessionResult(sessionDir, RecoveryOutcome.AlreadyRecorded,
                "Match already recorded for this session; staged duplicate reclaimed.");
        }

        return manifest is not null
            ? await RecoverOrphanAsync(sessionDir, sessionId, manifest, ct).ConfigureAwait(false)
            : await RecoverWithoutManifestAsync(sessionDir, sessionId, ct).ConfigureAwait(false);
    }

    /// <summary>Readable manifest, not finalized: the normal crash case.</summary>
    private async Task<RecoverySessionResult> RecoverOrphanAsync(string sessionDir, string sessionId, StagingManifest manifest, CancellationToken ct)
    {
        var video = ExistingOrFallback(manifest.VideoPath, sessionDir, "*.mp4");
        if (video is null)
        {
            TryDelete(sessionDir);
            return new RecoverySessionResult(sessionDir, RecoveryOutcome.NothingToRecover,
                "No staged video file; folder removed.");
        }
        var audio = ExistingOrFallback(manifest.AudioPath, sessionDir, "*.wav");
        var audioOffset = manifest is { AudioFirstSampleWallClock: { } a, VideoFirstFrameWallClock: { } v }
            ? a - v
            : TimeSpan.Zero;

        var outputPath = LibraryPaths.CreateSessionMp4Path(_settings.LibraryDir, manifest.StartedAt, sessionId);
        string? audioFallbackNote = null;
        try
        {
            // A crash-corrupted staged WAV must never strand the video: retry video-only.
            await MuxFallback.MuxWithVideoOnlyRetryAsync(
                _muxer, video, audio ?? string.Empty, audioOffset, outputPath,
                reason => audioFallbackNote = reason, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TryDeleteFile(outputPath);
            return new RecoverySessionResult(sessionDir, RecoveryOutcome.LeftInPlace,
                $"Mux failed; staged video preserved: {ex.Message}");
        }

        MatchRecord match;
        IReadOnlyList<MarkerRecord> markers;
        if (manifest.VideoFirstFrameWallClock is { } videoClock)
        {
            // Duration is unknown without probing the file (the app never invokes ffmpeg); Zero = unknown.
            var timeline = new RecordingTimeline(videoClock, outputPath, FileSize(outputPath), TimeSpan.Zero);
            (match, markers) = _assembler.Assemble(manifest.Events, timeline, VideoStatus.Incomplete);
        }
        else
        {
            // Missing clocks: no trustworthy marker offsets — register the video with empty markers.
            (match, _) = _assembler.Assemble(manifest.Events, null, VideoStatus.Incomplete);
            markers = [];
            match = match with
            {
                VideoStatus = VideoStatus.Incomplete,
                VideoPath = outputPath,
                VideoSizeBytes = FileSize(outputPath),
            };
        }

        return await InsertAndCleanUpAsync(sessionDir, sessionId, match, markers, outputPath, audioFallbackNote, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Corrupt or missing manifest. If a video exists it is still registered as Incomplete
    /// with minimal metadata rather than being lost.
    /// </summary>
    private async Task<RecoverySessionResult> RecoverWithoutManifestAsync(string sessionDir, string sessionId, CancellationToken ct)
    {
        var video = FindFile(sessionDir, "*.mp4");
        if (video is null)
        {
            TryDelete(sessionDir);
            return new RecoverySessionResult(sessionDir, RecoveryOutcome.NothingToRecover,
                "Manifest unreadable and no staged video; folder removed.");
        }
        var audio = FindFile(sessionDir, "*.wav");
        var startedAt = new DateTimeOffset(File.GetCreationTimeUtc(video), TimeSpan.Zero);

        var outputPath = LibraryPaths.CreateSessionMp4Path(_settings.LibraryDir, startedAt, sessionId);
        string? audioFallbackNote = null;
        try
        {
            // No clocks are known, so any staged audio is muxed at zero offset (best effort),
            // and a crash-corrupted staged WAV must never strand the video: retry video-only.
            await MuxFallback.MuxWithVideoOnlyRetryAsync(
                _muxer, video, audio ?? string.Empty, TimeSpan.Zero, outputPath,
                reason => audioFallbackNote = reason, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TryDeleteFile(outputPath);
            return new RecoverySessionResult(sessionDir, RecoveryOutcome.LeftInPlace,
                $"Manifest unreadable and mux failed; staged video preserved: {ex.Message}");
        }

        var match = new MatchRecord
        {
            StartedAt = startedAt,
            GameType = BgGameType.NotBattlegrounds, // truly unknown; the enum has no Unknown member
            PlayState = PlayState.Unknown,
            Truncated = true,
            VideoStatus = VideoStatus.Incomplete,
            VideoPath = outputPath,
            VideoSizeBytes = FileSize(outputPath),
        };
        return await InsertAndCleanUpAsync(sessionDir, sessionId, match, [], outputPath, audioFallbackNote, ct).ConfigureAwait(false);
    }

    private async Task<RecoverySessionResult> InsertAndCleanUpAsync(
        string sessionDir,
        string sessionId,
        MatchRecord match,
        IReadOnlyList<MarkerRecord> markers,
        string outputPath,
        string? audioFallbackNote,
        CancellationToken ct)
    {
        // Stamp the stable identity so this insert is idempotent and a later recovery pass over the
        // same folder (if the staging delete below fails) cannot create a duplicate row.
        match = match with { SessionId = string.IsNullOrEmpty(sessionId) ? null : sessionId };
        try
        {
            await _repository.InsertMatchAsync(match, markers, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The insert can fail because another writer already committed this session's row (e.g. a
            // uniqueness conflict). That row references the SAME deterministic output path, so deleting
            // the file would strand it. Only drop the library file when no row claims it; otherwise keep
            // the file and reclaim the staged duplicate.
            if (!string.IsNullOrEmpty(sessionId)
                && await _repository.MatchExistsBySessionAsync(sessionId, ct).ConfigureAwait(false))
            {
                TryDelete(sessionDir);
                return new RecoverySessionResult(sessionDir, RecoveryOutcome.AlreadyRecorded,
                    "Match already recorded for this session (insert conflict); staged duplicate reclaimed.");
            }
            // Genuine failure with no committed row: keep staging so a later run can retry; drop the
            // now-unreferenced library file.
            TryDeleteFile(outputPath);
            return new RecoverySessionResult(sessionDir, RecoveryOutcome.LeftInPlace,
                $"Match row insert failed; staged video preserved: {ex.Message}");
        }
        TryDelete(sessionDir);
        return new RecoverySessionResult(sessionDir, RecoveryOutcome.Recovered,
            audioFallbackNote is null
                ? $"Registered as incomplete: {outputPath}"
                : $"Registered as incomplete (video-only; staged audio was unreadable: {audioFallbackNote}): {outputPath}");
    }

    private static string? ExistingOrFallback(string manifestPath, string sessionDir, string pattern)
        => File.Exists(manifestPath) ? manifestPath : FindFile(sessionDir, pattern);

    private static string? FindFile(string dir, string pattern)
        => Directory.EnumerateFiles(dir, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    private static long FileSize(string path) => new FileInfo(path).Length;

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best effort; a leftover empty folder is harmless and retried next startup
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }
}

public enum RecoveryOutcome
{
    /// <summary>Media muxed into the library and a row inserted; staging removed.</summary>
    Recovered = 0,

    /// <summary>Manifest marked FinalizedCleanly; the row already exists and the staged duplicate was reclaimed.</summary>
    SkippedFinalized = 1,

    /// <summary>No video to save; the empty folder was removed.</summary>
    NothingToRecover = 2,

    /// <summary>Recovery failed; the staged files were preserved for a later attempt.</summary>
    LeftInPlace = 3,

    /// <summary>A row already existed for this session id (idempotent no-op); the staged duplicate was reclaimed.</summary>
    AlreadyRecorded = 4,
}

public sealed record RecoverySessionResult(string SessionDir, RecoveryOutcome Outcome, string? Detail = null);

public sealed record RecoveryReport(IReadOnlyList<RecoverySessionResult> Sessions);
