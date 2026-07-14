namespace BgRecorder.Core.Capture;

/// <summary>Video capture behind an interface so the route can pivot (ScreenRecorderLib → ffmpeg → libobs).</summary>
public interface IRecorder
{
    /// <summary>Start recording the target window to a staging path. Video only; audio is a separate capture.</summary>
    Task<IRecordingSession> StartAsync(RecordingTarget target, VideoOptions options, string stagingMp4Path, CancellationToken ct);
}

public interface IRecordingSession : IAsyncDisposable
{
    /// <summary>Wall clock of the first encoded frame, once known. Marker/mux offsets are computed against this.</summary>
    DateTimeOffset? FirstFrameWallClock { get; }

    event Action<RecorderStatus>? StatusChanged;
    event Action<string>? Failed;

    /// <summary>Graceful stop; finalizes the (fragmented) MP4 and returns what was written.</summary>
    Task<RecordingResult> StopAsync();
}

public enum RecorderStatus
{
    Idle = 0,
    Recording = 1,
    Finalizing = 2,
    Failed = 3,
}

/// <summary>The window to record, bound to the game process (never a tracker overlay with a similar title).</summary>
public sealed record RecordingTarget(int ProcessId, string WindowTitleHint);

public sealed record VideoOptions
{
    public int Fps { get; init; } = 60;
    public int BitrateMbps { get; init; } = 12;
    public bool FragmentedMp4 { get; init; } = true;
}

public sealed record RecordingResult(string Path, TimeSpan Duration, long SizeBytes);
