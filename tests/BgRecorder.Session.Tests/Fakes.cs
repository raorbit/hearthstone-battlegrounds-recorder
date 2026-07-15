using BgRecorder.Core.Audio;
using BgRecorder.Core.Capture;
using BgRecorder.Core.Data;
using BgRecorder.Core.Events;
using BgRecorder.Core.Session;
using BgRecorder.Session;

namespace BgRecorder.Session.Tests;

internal sealed class FakeGameEventSource : IGameEventSource
{
    public event Action<GameEvent>? EventReceived;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public void Raise(GameEvent e) => EventReceived?.Invoke(e);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeRecorder : IRecorder
{
    public bool ThrowOnStart;
    public bool StopThrows;
    public DateTimeOffset? FirstFrameWallClock;
    public TimeSpan StopDuration = TimeSpan.FromMinutes(10);
    public int StartCount;
    public FakeRecordingSession? LastSession;

    public Task<IRecordingSession> StartAsync(RecordingTarget target, VideoOptions options, string stagingMp4Path, CancellationToken ct)
    {
        StartCount++;
        if (ThrowOnStart)
        {
            throw new InvalidOperationException("recorder start failed");
        }
        File.WriteAllBytes(stagingMp4Path, new byte[64]);
        LastSession = new FakeRecordingSession(stagingMp4Path)
        {
            FirstFrameWallClock = FirstFrameWallClock,
            StopThrows = StopThrows,
            StopDuration = StopDuration,
        };
        return Task.FromResult<IRecordingSession>(LastSession);
    }
}

internal sealed class FakeRecordingSession(string path) : IRecordingSession
{
    public DateTimeOffset? FirstFrameWallClock { get; set; }
    public bool StopThrows { get; set; }
    public TimeSpan StopDuration { get; set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }

    public event Action<RecorderStatus>? StatusChanged;
    public event Action<string>? Failed;

    public void RaiseStatus(RecorderStatus status) => StatusChanged?.Invoke(status);

    public void RaiseFailed(string reason) => Failed?.Invoke(reason);

    public Task<RecordingResult> StopAsync()
    {
        Stopped = true;
        if (StopThrows)
        {
            throw new InvalidOperationException("video stop failed");
        }
        return Task.FromResult(new RecordingResult(path, StopDuration, new FileInfo(path).Length));
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeAudioCapture : IAudioCapture
{
    public bool ThrowOnStart;
    public bool StopThrows;
    public DateTimeOffset? FirstSampleWallClock;
    public TimeSpan StopDuration = TimeSpan.FromMinutes(10);
    public int StartCount;
    public FakeAudioSession? LastSession;
    /// <summary>When set, the session reports this as its actual mode (to simulate a downgrade); otherwise it mirrors the requested mode.</summary>
    public AudioCaptureMode? ForceActualMode;

    public Task<IAudioSession> StartAsync(AudioTarget target, string stagingWavPath, CancellationToken ct)
    {
        StartCount++;
        if (ThrowOnStart)
        {
            throw new InvalidOperationException("audio start failed");
        }
        File.WriteAllBytes(stagingWavPath, new byte[32]);
        LastSession = new FakeAudioSession(stagingWavPath)
        {
            FirstSampleWallClock = FirstSampleWallClock,
            StopThrows = StopThrows,
            StopDuration = StopDuration,
            ActualMode = ForceActualMode ?? target.Mode,
        };
        return Task.FromResult<IAudioSession>(LastSession);
    }
}

internal sealed class FakeAudioSession(string path) : IAudioSession
{
    public DateTimeOffset? FirstSampleWallClock { get; set; }
    public AudioCaptureMode ActualMode { get; set; } = AudioCaptureMode.ProcessLoopback;
    public bool StopThrows { get; set; }
    public TimeSpan StopDuration { get; set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }

    public event Action<AudioFailure>? Failed;

    public void RaiseFailed(string reason, AudioStreamKind source = AudioStreamKind.Game)
        => Failed?.Invoke(new AudioFailure(source, reason));

    public Task<AudioResult> StopAsync()
    {
        Stopped = true;
        if (StopThrows)
        {
            throw new InvalidOperationException("audio stop failed");
        }
        return Task.FromResult(new AudioResult(path, StopDuration));
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeMuxer : IMuxer
{
    public bool Throw;
    /// <summary>Throws only when a non-empty audio path is supplied — models a crash-corrupted staged WAV the muxer rejects.</summary>
    public bool ThrowOnAudio;
    /// <summary>Optional artificial mux duration so tests can queue commands while Finalizing runs.</summary>
    public TimeSpan Delay = TimeSpan.Zero;
    public readonly List<(string Video, string Audio, TimeSpan Offset, string Output)> Calls = [];

    public async Task MuxAsync(string videoMp4, string audioWav, TimeSpan audioOffset, string outputMp4, CancellationToken ct)
    {
        Calls.Add((videoMp4, audioWav, audioOffset, outputMp4));
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }
        if (Throw)
        {
            throw new InvalidOperationException("mux failed");
        }
        if (ThrowOnAudio && audioWav.Length > 0)
        {
            throw new InvalidOperationException("byte stream type of the given URL is unsupported");
        }
        File.WriteAllBytes(outputMp4, new byte[128]);
    }
}

internal sealed class FakeAssembler : IMatchAssembler
{
    public bool Throw;
    public readonly List<(IReadOnlyList<GameEvent> Events, RecordingTimeline? Timeline, VideoStatus Status)> Calls = [];

    public (MatchRecord Match, IReadOnlyList<MarkerRecord> Markers) Assemble(
        IReadOnlyList<GameEvent> events,
        RecordingTimeline? timeline,
        VideoStatus videoStatus)
    {
        Calls.Add((events, timeline, videoStatus));
        if (Throw)
        {
            throw new InvalidOperationException("assemble failed");
        }
        var match = new MatchRecord
        {
            StartedAt = events.Count > 0 ? events[0].Timestamp : DateTimeOffset.Now,
            GameType = events.OfType<GameTypeResolved>().FirstOrDefault()?.GameType ?? BgGameType.Solo,
            VideoStatus = videoStatus,
            VideoPath = timeline?.FinalVideoPath,
            VideoSizeBytes = timeline?.SizeBytes,
            VideoDuration = timeline?.Duration,
            Truncated = events.OfType<MatchEnded>().LastOrDefault()?.Truncated ?? false,
        };
        return (match, Array.Empty<MarkerRecord>());
    }
}

internal sealed class FakeRepository : IMatchRepository
{
    public bool ThrowOnInsert;
    public readonly List<(MatchRecord Match, IReadOnlyList<MarkerRecord> Markers)> Inserted = [];
    public IReadOnlyList<MatchRecord> Matches = [];
    /// <summary>Session ids that MatchExistsBySessionAsync should report as already present (simulates a committed row from a prior run).</summary>
    public readonly HashSet<string> ExistingSessions = [];

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<long> InsertMatchAsync(MatchRecord match, IReadOnlyList<MarkerRecord> markers, CancellationToken ct = default)
    {
        if (ThrowOnInsert)
        {
            throw new InvalidOperationException("insert failed");
        }
        // Model the repository's idempotency: a re-insert of the same session returns the prior id.
        if (match.SessionId is { } sid)
        {
            var existingIndex = Inserted.FindIndex(x => x.Match.SessionId == sid);
            if (existingIndex >= 0)
            {
                return Task.FromResult((long)(existingIndex + 1));
            }
        }
        Inserted.Add((match, markers));
        return Task.FromResult((long)Inserted.Count);
    }

    public Task<bool> MatchExistsBySessionAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(ExistingSessions.Contains(sessionId) || Inserted.Any(x => x.Match.SessionId == sessionId));

    public Task UpdateVideoStatusAsync(long matchId, VideoStatus status, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<MatchRecord>> ListMatchesAsync(CancellationToken ct = default) => Task.FromResult(Matches);
}

internal sealed class FakeDiskSafety : IDiskSafety
{
    public ArmCheckResult ArmResult = new(true, null);
    public Action? LowSpaceCallback;
    public int WatchdogStartCount;
    public int WatchdogDisposeCount;
    /// <summary>Makes the watchdog Dispose throw — an unguarded early step of FinalizeAsync — to exercise the finally-block disposal.</summary>
    public bool WatchdogDisposeThrows;

    public ArmCheckResult CheckCanArm() => ArmResult;

    public IDisposable StartWatchdog(Action onLowSpace)
    {
        WatchdogStartCount++;
        LowSpaceCallback = onLowSpace;
        return new Handle(this);
    }

    private sealed class Handle(FakeDiskSafety owner) : IDisposable
    {
        public void Dispose()
        {
            owner.WatchdogDisposeCount++;
            if (owner.WatchdogDisposeThrows)
            {
                throw new InvalidOperationException("watchdog dispose failed");
            }
        }
    }
}

internal sealed class FakeGameProcessLocator : IGameProcessLocator
{
    public RecordingTarget? Target = new(4321, "Hearthstone");

    public RecordingTarget? FindGame() => Target;
}

internal sealed class FakeFreeSpaceProbe : IFreeSpaceProbe
{
    public long Free;
    public Exception? Throws;

    public long GetAvailableFreeBytes(string path)
    {
        if (Throws is not null)
        {
            throw Throws;
        }
        return Free;
    }
}
