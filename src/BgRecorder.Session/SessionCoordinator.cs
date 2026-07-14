using System.Threading.Channels;
using BgRecorder.Core;
using BgRecorder.Core.Audio;
using BgRecorder.Core.Capture;
using BgRecorder.Core.Data;
using BgRecorder.Core.Events;
using BgRecorder.Core.Session;

namespace BgRecorder.Session;

/// <summary>
/// The recording state machine. All inputs — game events, user commands, watchdog and
/// capture-failure callbacks — are posted to a single unbounded channel consumed by one
/// loop task, so every handler runs serialized with no locks and no async-void.
///
/// Flow: Armed + MatchStarted buffers events; GameTypeResolved(Solo/Duos) passes the disk
/// floor check and starts video+audio into a fresh staging session folder with an atomic
/// crash-recovery manifest (throttled rewrites as events arrive). MatchEnded / manual stop /
/// low-space watchdog finalizes: stop audio then video (either may fail; a dead audio session
/// never aborts the video), mux into the library, assemble + insert the match row, mark the
/// manifest FinalizedCleanly, delete staging. Anything that goes wrong mid-finalize leaves the
/// staged files in place for <see cref="StartupRecovery"/> — data is never deleted on a failure path.
/// </summary>
public sealed class SessionCoordinator : ISessionCoordinator
{
    private static readonly TimeSpan ManifestWriteInterval = TimeSpan.FromSeconds(1);

    private readonly IGameEventSource _source;
    private readonly IRecorder _recorder;
    private readonly IAudioCapture _audioCapture;
    private readonly IMuxer _muxer;
    private readonly IMatchAssembler _assembler;
    private readonly IMatchRepository _repository;
    private readonly IDiskSafety _diskSafety;
    private readonly IGameProcessLocator _locator;
    private readonly AppSettings _settings;

    private readonly Channel<Command> _commands =
        Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });

    private Task? _loop;
    private bool _started;
    private bool _disposed;

    // ---- State below is owned exclusively by the loop task. ----
    private CoordinatorState _state = CoordinatorState.GameNotFound;
    private bool _pauseRequested;
    private List<GameEvent>? _matchEvents; // non-null from MatchStarted until the match resolves/finalizes
    private RecordingContext? _recording;
    private readonly List<TaskCompletionSource> _stopWaiters = [];

    public SessionCoordinator(
        IGameEventSource source,
        IRecorder recorder,
        IAudioCapture audioCapture,
        IMuxer muxer,
        IMatchAssembler assembler,
        IMatchRepository repository,
        IDiskSafety diskSafety,
        IGameProcessLocator locator,
        AppSettings settings)
    {
        _source = source;
        _recorder = recorder;
        _audioCapture = audioCapture;
        _muxer = muxer;
        _assembler = assembler;
        _repository = repository;
        _diskSafety = diskSafety;
        _locator = locator;
        _settings = settings;
    }

    public CoordinatorState State => _state;

    public event Action<CoordinatorState>? StateChanged;

    /// <summary>
    /// Human-readable operational notes — arm refusals (with the disk-safety reason), audio
    /// loss, finalize problems. The App logs these and surfaces them in the tray; the library
    /// itself never writes to the console.
    /// </summary>
    public event Action<string>? Diagnostic;

    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("The coordinator is already started.");
        }
        _started = true;
        _source.EventReceived += OnGameEvent;
        await _source.StartAsync(ct).ConfigureAwait(false);
        _loop = Task.Run(RunLoopAsync, CancellationToken.None);
        Post(new InitializeCmd());
    }

    public Task StopCurrentRecordingAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(new StopCurrentCmd(tcs)))
        {
            tcs.TrySetResult(); // already shut down; nothing is recording
        }
        return tcs.Task;
    }

    public void PauseAutoRecording() => Post(new PauseCmd());

    public void ResumeNow() => Post(new ResumeCmd());

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _source.EventReceived -= OnGameEvent;
        if (_loop is not null)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(new ShutdownCmd(done));
            _commands.Writer.TryComplete();
            await _loop.ConfigureAwait(false);
        }
        else
        {
            _commands.Writer.TryComplete();
        }
    }

    private void OnGameEvent(GameEvent e) => Post(new GameEventCmd(e));

    private bool Post(Command cmd) => _commands.Writer.TryWrite(cmd);

    // ---------------------------------------------------------------- event loop

    private async Task RunLoopAsync()
    {
        await foreach (var cmd in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (cmd)
                {
                    case InitializeCmd:
                        SetState(_locator.FindGame() is null ? CoordinatorState.GameNotFound : CoordinatorState.Armed);
                        break;
                    case GameEventCmd(var e):
                        await HandleGameEventAsync(e).ConfigureAwait(false);
                        break;
                    case StopCurrentCmd(var tcs):
                        await HandleStopCurrentAsync(tcs).ConfigureAwait(false);
                        break;
                    case PauseCmd:
                        HandlePause();
                        break;
                    case ResumeCmd:
                        HandleResume();
                        break;
                    case LowSpaceCmd:
                        if (_recording is not null)
                        {
                            Report("Low disk space on the staging volume; finalizing the recording early.");
                            await FinalizeAsync().ConfigureAwait(false);
                        }
                        break;
                    case VideoFailedCmd(var reason):
                        if (_recording is not null)
                        {
                            Report($"Video capture failed mid-recording: {reason}");
                            await FinalizeAsync().ConfigureAwait(false);
                        }
                        break;
                    case AudioFailedCmd(var reason):
                        if (_recording is not null && !_recording.AudioDead)
                        {
                            _recording.AudioDead = true;
                            Report($"Audio capture failed mid-recording; the video continues and will be muxed without audio: {reason}");
                        }
                        break;
                    case ClocksMaybeKnownCmd:
                        if (_recording is not null)
                        {
                            WriteManifestThrottled();
                        }
                        break;
                    case FlushManifestCmd:
                        if (_recording is not null)
                        {
                            _recording.FlushScheduled = false;
                            WriteManifestNow();
                        }
                        break;
                    case ShutdownCmd(var done):
                        if (_recording is not null)
                        {
                            await FinalizeAsync().ConfigureAwait(false);
                        }
                        done.TrySetResult();
                        return;
                }
            }
            catch (Exception ex)
            {
                Report($"Unhandled coordinator error handling {cmd.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task HandleGameEventAsync(GameEvent e)
    {
        switch (e)
        {
            case LogSessionChanged:
                if (_state == CoordinatorState.GameNotFound)
                {
                    SetState(CoordinatorState.Armed);
                }
                break;

            case MatchStarted:
                if (_recording is not null)
                {
                    // A new CREATE_GAME without a terminal state for the previous match:
                    // treat the running recording as truncated and finalize what we have.
                    Report("A new match started while still recording; finalizing the previous match.");
                    await FinalizeAsync().ConfigureAwait(false);
                }
                if (_state is CoordinatorState.Armed or CoordinatorState.GameNotFound)
                {
                    if (_state == CoordinatorState.GameNotFound)
                    {
                        SetState(CoordinatorState.Armed); // events flowing means the game is back
                    }
                    _matchEvents = [e];
                }
                break;

            case GameTypeResolved resolved:
                if (_recording is not null)
                {
                    AppendEvent(e);
                    break;
                }
                if (_matchEvents is null)
                {
                    break; // no match being buffered (paused, or type resolved twice)
                }
                _matchEvents.Add(e);
                if (resolved.GameType == BgGameType.NotBattlegrounds)
                {
                    _matchEvents = null; // not our mode: ignore the whole match, stay armed
                    break;
                }
                await TryStartRecordingAsync().ConfigureAwait(false);
                break;

            case MatchEnded:
                if (_recording is not null)
                {
                    AppendEvent(e);
                    await FinalizeAsync().ConfigureAwait(false);
                }
                else
                {
                    _matchEvents = null; // buffered match that never became a recording
                }
                break;

            default:
                AppendEvent(e);
                break;
        }
    }

    private void AppendEvent(GameEvent e)
    {
        if (_matchEvents is null)
        {
            return;
        }
        _matchEvents.Add(e);
        if (_recording is not null)
        {
            WriteManifestThrottled();
        }
    }

    private async Task HandleStopCurrentAsync(TaskCompletionSource tcs)
    {
        if (_recording is null)
        {
            tcs.TrySetResult();
            return;
        }
        try
        {
            _stopWaiters.Add(tcs);
            await FinalizeAsync().ConfigureAwait(false);
        }
        finally
        {
            tcs.TrySetResult();
        }
    }

    private void HandlePause()
    {
        if (_recording is not null)
        {
            _pauseRequested = true; // the current recording finishes; no new arms after
        }
        else if (_state is CoordinatorState.Armed or CoordinatorState.GameNotFound)
        {
            // Disarm any match buffered between MatchStarted and GameTypeResolved too,
            // otherwise the pending GameTypeResolved would start a recording while Paused.
            _matchEvents = null;
            SetState(CoordinatorState.Paused);
        }
    }

    private void HandleResume()
    {
        _pauseRequested = false; // cancels a pending mid-recording pause too
        if (_state == CoordinatorState.Paused)
        {
            SetState(_locator.FindGame() is null ? CoordinatorState.GameNotFound : CoordinatorState.Armed);
        }
    }

    // ---------------------------------------------------------------- recording

    private async Task TryStartRecordingAsync()
    {
        var events = _matchEvents!;
        var matchStartedAt = events.OfType<MatchStarted>().FirstOrDefault()?.Timestamp ?? events[0].Timestamp;

        var check = _diskSafety.CheckCanArm();
        if (!check.CanArm)
        {
            Report($"Not recording this match: {check.Reason ?? "insufficient free disk space"}");
            _matchEvents = null;
            return; // stay Armed; the next match re-checks
        }

        var target = _locator.FindGame();
        if (target is null)
        {
            Report("Not recording this match: the game window could not be found.");
            _matchEvents = null;
            return;
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var sessionDir = Path.Combine(_settings.StagingDir, sessionId);
        var videoPath = Path.Combine(sessionDir, "video.mp4");
        var audioPath = Path.Combine(sessionDir, "audio.wav");

        IRecordingSession video;
        try
        {
            Directory.CreateDirectory(sessionDir);
            var options = new VideoOptions
            {
                Fps = _settings.Fps,
                BitrateMbps = _settings.BitrateMbps,
                FragmentedMp4 = true, // a crash mid-match must still leave a playable file
            };
            video = await _recorder.StartAsync(target, options, videoPath, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report($"Could not start video capture: {ex.Message}");
            TryDeleteDirectory(sessionDir);
            _matchEvents = null;
            return;
        }

        var ctx = new RecordingContext
        {
            SessionId = sessionId,
            SessionDir = sessionDir,
            VideoPath = videoPath,
            AudioPath = audioPath,
            Video = video,
            MatchStartedAt = matchStartedAt,
            RecordingStartedAt = DateTimeOffset.Now,
        };
        ctx.VideoFailedHandler = reason => Post(new VideoFailedCmd(reason));
        video.Failed += ctx.VideoFailedHandler;
        ctx.VideoStatusHandler = _ => Post(new ClocksMaybeKnownCmd()); // FirstFrameWallClock has no event of its own
        video.StatusChanged += ctx.VideoStatusHandler;

        try
        {
            var mode = _settings.GameOnlyAudio ? AudioCaptureMode.ProcessLoopback : AudioCaptureMode.SystemLoopback;
            var audioTarget = new AudioTarget(target.ProcessId, IncludeProcessTree: true, mode)
            {
                MixMicrophone = _settings.MixMicrophone,
            };
            ctx.Audio = await _audioCapture.StartAsync(audioTarget, audioPath, CancellationToken.None).ConfigureAwait(false);
            ctx.AudioFailedHandler = reason => Post(new AudioFailedCmd(reason));
            ctx.Audio.Failed += ctx.AudioFailedHandler;
        }
        catch (Exception ex)
        {
            // Audio must never block recording: proceed video-only.
            ctx.Audio = null;
            ctx.AudioDead = true;
            Report($"Audio capture unavailable; recording video only: {ex.Message}");
        }

        _recording = ctx;
        WriteManifestNow();
        ctx.Watchdog = _diskSafety.StartWatchdog(() => Post(new LowSpaceCmd()));
        SetState(CoordinatorState.Recording);
    }

    private async Task FinalizeAsync()
    {
        var ctx = _recording!;
        var events = (_matchEvents ?? []).ToList();
        SetState(CoordinatorState.Finalizing);

        try
        {
            ctx.Watchdog?.Dispose();
            Unsubscribe(ctx);

            // Stop audio first. A failed/dead audio session must never abort the video finalize.
            AudioResult? audioResult = null;
            if (ctx.Audio is not null && !ctx.AudioDead)
            {
                try
                {
                    audioResult = await ctx.Audio.StopAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Report($"Audio finalize failed; muxing video only: {ex.Message}");
                }
            }
            var audioClock = TryGetClock(() => ctx.Audio?.FirstSampleWallClock);

            RecordingResult? videoResult = null;
            try
            {
                videoResult = await ctx.Video.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Report($"Video finalize failed: {ex.Message}");
            }
            var videoClock = TryGetClock(() => ctx.Video.FirstFrameWallClock);

            // Final pre-mux manifest snapshot: if anything below fails, recovery has everything.
            WriteManifestCore(ctx, events, finalized: false);

            var finalized = false;
            if (videoResult is not null && File.Exists(videoResult.Path))
            {
                finalized = await MuxAndInsertAsync(ctx, events, videoResult, audioResult, videoClock, audioClock)
                    .ConfigureAwait(false);
            }
            else
            {
                Report("No video was finalized; staged files kept for startup recovery.");
            }

            if (finalized)
            {
                WriteManifestCore(ctx, events, finalized: true);
                TryDeleteDirectory(ctx.SessionDir);
            }
        }
        catch (Exception ex)
        {
            Report($"Finalize failed; staged files kept for startup recovery: {ex.Message}");
        }
        finally
        {
            // The capture sessions hold native encoder/WASAPI resources: release them on
            // every path, including an exception escaping the try above.
            await DisposeQuietlyAsync(ctx.Audio).ConfigureAwait(false);
            await DisposeQuietlyAsync(ctx.Video).ConfigureAwait(false);

            _recording = null;
            _matchEvents = null;

            var next = _pauseRequested
                ? CoordinatorState.Paused
                : _locator.FindGame() is null
                    ? CoordinatorState.GameNotFound
                    : CoordinatorState.Armed;
            _pauseRequested = false;
            SetState(next);

            foreach (var waiter in _stopWaiters)
            {
                waiter.TrySetResult();
            }
            _stopWaiters.Clear();
        }
    }

    /// <summary>
    /// Mux staging → library, assemble, insert. True only when the row is committed. Never
    /// throws: every failure step reports, drops the now-unreferenced library file, and
    /// returns false so the caller keeps the staged files for <see cref="StartupRecovery"/>.
    /// </summary>
    private async Task<bool> MuxAndInsertAsync(
        RecordingContext ctx,
        List<GameEvent> events,
        RecordingResult videoResult,
        AudioResult? audioResult,
        DateTimeOffset? videoClock,
        DateTimeOffset? audioClock)
    {
        var audioUsable = audioResult is not null && File.Exists(audioResult.Path);
        var audioOffset = audioUsable && audioClock is not null && videoClock is not null
            ? audioClock.Value - videoClock.Value
            : TimeSpan.Zero;

        string outputPath;
        try
        {
            outputPath = LibraryPaths.CreateUniqueMp4Path(_settings.LibraryDir, ctx.MatchStartedAt);
        }
        catch (Exception ex)
        {
            // e.g. the library volume is unplugged or permission-denied.
            Report($"Could not create the library folder; staged files kept for startup recovery: {ex.Message}");
            return false;
        }

        try
        {
            await MuxFallback.MuxWithVideoOnlyRetryAsync(
                _muxer,
                videoResult.Path,
                audioUsable ? audioResult!.Path : string.Empty,
                audioOffset,
                outputPath,
                reason => Report($"Muxing with the staged audio failed; retrying video-only: {reason}"),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report($"Mux failed; staged files kept for startup recovery: {ex.Message}");
            TryDeleteFile(outputPath);
            return false;
        }

        MatchRecord match;
        IReadOnlyList<MarkerRecord> markers;
        try
        {
            var timeline = new RecordingTimeline(
                videoClock ?? ctx.RecordingStartedAt,
                outputPath,
                new FileInfo(outputPath).Length,
                videoResult.Duration);
            (match, markers) = _assembler.Assemble(events, timeline, VideoStatus.Complete);
        }
        catch (Exception ex)
        {
            Report($"Could not assemble the match record; staged files kept for startup recovery: {ex.Message}");
            TryDeleteFile(outputPath);
            return false;
        }

        try
        {
            await _repository.InsertMatchAsync(match, markers, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // Keep staging (recovery re-runs the whole finalize); drop the now-unreferenced library file.
            Report($"Could not save the match row; staged files kept for startup recovery: {ex.Message}");
            TryDeleteFile(outputPath);
            return false;
        }
    }

    // ---------------------------------------------------------------- manifest

    private void WriteManifestThrottled()
    {
        var ctx = _recording!;
        var sinceLast = DateTimeOffset.UtcNow - ctx.LastManifestWriteUtc;
        if (sinceLast >= ManifestWriteInterval)
        {
            WriteManifestNow();
        }
        else if (!ctx.FlushScheduled)
        {
            // Defer instead of dropping, so a crash loses at most ~1s of events.
            ctx.FlushScheduled = true;
            _ = FlushLaterAsync(ManifestWriteInterval - sinceLast);
        }
    }

    private async Task FlushLaterAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay).ConfigureAwait(false);
            Post(new FlushManifestCmd());
        }
        catch
        {
            // best-effort deferred flush; the finalize path always writes unthrottled
        }
    }

    private void WriteManifestNow()
    {
        var ctx = _recording!;
        WriteManifestCore(ctx, _matchEvents ?? [], finalized: false);
    }

    private void WriteManifestCore(RecordingContext ctx, List<GameEvent> events, bool finalized)
    {
        try
        {
            ManifestStore.Write(ctx.SessionDir, new StagingManifest
            {
                SessionId = ctx.SessionId,
                StartedAt = ctx.MatchStartedAt,
                VideoPath = ctx.VideoPath,
                AudioPath = ctx.AudioPath,
                VideoFirstFrameWallClock = TryGetClock(() => ctx.Video.FirstFrameWallClock),
                AudioFirstSampleWallClock = TryGetClock(() => ctx.Audio?.FirstSampleWallClock),
                Events = events.ToArray(),
                FinalizedCleanly = finalized,
            });
            ctx.LastManifestWriteUtc = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            Report($"Manifest write failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- helpers

    private void SetState(CoordinatorState state)
    {
        if (_state == state)
        {
            return;
        }
        _state = state;
        StateChanged?.Invoke(state);
    }

    private void Report(string message) => Diagnostic?.Invoke(message);

    private static void Unsubscribe(RecordingContext ctx)
    {
        if (ctx.VideoStatusHandler is not null)
        {
            ctx.Video.StatusChanged -= ctx.VideoStatusHandler;
        }
        if (ctx.VideoFailedHandler is not null)
        {
            ctx.Video.Failed -= ctx.VideoFailedHandler;
        }
        if (ctx.Audio is not null && ctx.AudioFailedHandler is not null)
        {
            ctx.Audio.Failed -= ctx.AudioFailedHandler;
        }
    }

    private static DateTimeOffset? TryGetClock(Func<DateTimeOffset?> get)
    {
        try
        {
            return get();
        }
        catch
        {
            return null; // a dead capture session may throw; treat its clock as unknown
        }
    }

    private static async ValueTask DisposeQuietlyAsync(IAsyncDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // a session that fails to dispose after stop has nothing left we need
        }
    }

    private void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Report($"Could not delete staging folder '{dir}': {ex.Message}");
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Report($"Could not delete file '{path}': {ex.Message}");
        }
    }

    /// <summary>Everything owned by one in-flight recording; loop-owned, never shared.</summary>
    private sealed class RecordingContext
    {
        public required string SessionId { get; init; }
        public required string SessionDir { get; init; }
        public required string VideoPath { get; init; }
        public required string AudioPath { get; init; }
        public required IRecordingSession Video { get; init; }
        public required DateTimeOffset MatchStartedAt { get; init; }
        public required DateTimeOffset RecordingStartedAt { get; init; }
        public IAudioSession? Audio { get; set; }
        public bool AudioDead { get; set; }
        public IDisposable? Watchdog { get; set; }
        public DateTimeOffset LastManifestWriteUtc { get; set; }
        public bool FlushScheduled { get; set; }
        public Action<RecorderStatus>? VideoStatusHandler { get; set; }
        public Action<string>? VideoFailedHandler { get; set; }
        public Action<string>? AudioFailedHandler { get; set; }
    }

    private abstract record Command;

    private sealed record InitializeCmd : Command;

    private sealed record GameEventCmd(GameEvent Event) : Command;

    private sealed record StopCurrentCmd(TaskCompletionSource Done) : Command;

    private sealed record PauseCmd : Command;

    private sealed record ResumeCmd : Command;

    private sealed record LowSpaceCmd : Command;

    private sealed record VideoFailedCmd(string Reason) : Command;

    private sealed record AudioFailedCmd(string Reason) : Command;

    private sealed record ClocksMaybeKnownCmd : Command;

    private sealed record FlushManifestCmd : Command;

    private sealed record ShutdownCmd(TaskCompletionSource Done) : Command;
}
