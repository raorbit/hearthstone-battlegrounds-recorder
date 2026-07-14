using BgRecorder.Core.Capture;

namespace BgRecorder.Capture.Internal;

/// <summary>
/// Wraps one <see cref="ISrlRecorder"/> run as an <see cref="IRecordingSession"/>: maps library
/// events onto the Core status/failure surface, stamps the first encoded frame's wall clock,
/// and derives the <see cref="RecordingResult"/> at stop. Deliberately holds no ScreenRecorderLib
/// types so it is exercised by tests through a fake recorder.
/// </summary>
internal sealed class RecordingSessionImpl : IRecordingSession
{
    /// <summary>Cap on how long a graceful stop waits for the library to finalize the container.</summary>
    private static readonly TimeSpan StopFinalizeTimeout = TimeSpan.FromSeconds(30);

    private readonly ISrlRecorder _recorder;
    private readonly string _stagingPath;
    private readonly object _gate = new();

    private readonly TaskCompletionSource _ended =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<RecordingResult> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private DateTimeOffset _recordCallWallClock;
    private DateTimeOffset? _firstFrameWallClock;
    private DateTimeOffset? _recordingStatusWallClock;
    private DateTimeOffset? _endWallClock;
    private string? _completedPath;

    private int _stopRequested;
    private int _disposed;

    public RecordingSessionImpl(ISrlRecorder recorder, string stagingPath)
    {
        _recorder = recorder;
        _stagingPath = stagingPath;

        _recorder.StatusChanged += OnStatusChanged;
        _recorder.FrameRecorded += OnFrameRecorded;
        _recorder.Completed += OnCompleted;
        _recorder.Failed += OnFailed;
    }

    /// <summary>
    /// Best available wall clock of the first encoded frame. The <c>OnFrameRecorded</c> stamp is
    /// frame-accurate; before the first frame arrives this falls back to the Recording-status stamp,
    /// which precedes the first encoded frame (Spike B measured ~0.5 s of startup latency), so the
    /// fallback is an <em>early</em> estimate and the mux offset it feeds is biased earlier by that much.
    /// </summary>
    public DateTimeOffset? FirstFrameWallClock
    {
        get
        {
            lock (_gate)
                return _firstFrameWallClock ?? _recordingStatusWallClock;
        }
    }

    public event Action<RecorderStatus>? StatusChanged;
    public event Action<string>? Failed;

    /// <summary>Stamp the start and begin recording. Called once by the owning <see cref="IRecorder"/>.</summary>
    public void Start()
    {
        _recordCallWallClock = DateTimeOffset.UtcNow;
        _recorder.Record(_stagingPath);
    }

    public Task<RecordingResult> StopAsync()
    {
        // Idempotent: only the first caller drives the stop; later callers await the same result.
        if (Interlocked.Exchange(ref _stopRequested, 1) == 1)
            return _result.Task;

        _ = StopCoreAsync();
        return _result.Task;
    }

    private async Task StopCoreAsync()
    {
        lock (_gate)
            _endWallClock ??= DateTimeOffset.UtcNow; // capture stops here; frames after this aren't in the file

        StatusChanged?.Invoke(RecorderStatus.Finalizing);

        try
        {
            _recorder.Stop();
        }
        catch
        {
            // Already stopped or failed mid-flight: still finalize whatever is on disk below.
        }

        // Wait for the library to finish finalizing, but never hang the caller forever.
        await Task.WhenAny(_ended.Task, Task.Delay(StopFinalizeTimeout)).ConfigureAwait(false);

        _result.TrySetResult(BuildResult());
    }

    private RecordingResult BuildResult()
    {
        string path = _completedPath ?? _stagingPath;

        long size = 0;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists)
                size = fi.Length;
        }
        catch
        {
            // Path unreadable: report zero bytes rather than throwing out of a stop.
        }

        DateTimeOffset end;
        DateTimeOffset start;
        lock (_gate)
        {
            end = _endWallClock ?? DateTimeOffset.UtcNow;
            start = _firstFrameWallClock ?? _recordingStatusWallClock ?? _recordCallWallClock;
        }

        var duration = end - start;
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        return new RecordingResult(path, duration, size);
    }

    private void OnStatusChanged(SrlStatus status)
    {
        if (status == SrlStatus.Recording)
        {
            lock (_gate)
                _recordingStatusWallClock ??= DateTimeOffset.UtcNow;
        }

        var mapped = Map(status);
        if (mapped is { } coreStatus)
            StatusChanged?.Invoke(coreStatus);
    }

    private void OnFrameRecorded(int frameNumber, long timestamp)
    {
        bool first = false;
        lock (_gate)
        {
            if (_firstFrameWallClock is null)
            {
                _firstFrameWallClock = DateTimeOffset.UtcNow;
                first = true;
            }
        }

        if (first)
        {
            // First frame is stamped; shed the preview overhead for the rest of the recording.
            _recorder.TryDisableFramePreview();
        }
    }

    private void OnCompleted(string filePath)
    {
        lock (_gate)
        {
            _completedPath = string.IsNullOrEmpty(filePath) ? _completedPath : filePath;
            _endWallClock ??= DateTimeOffset.UtcNow;
        }
        _ended.TrySetResult();
    }

    private void OnFailed(string error)
    {
        lock (_gate)
            _endWallClock ??= DateTimeOffset.UtcNow;

        StatusChanged?.Invoke(RecorderStatus.Failed);
        Failed?.Invoke(error);

        // Unblock any pending/future stop; the fragmented MP4 on disk is still finalized into a result.
        _ended.TrySetResult();
    }

    private static RecorderStatus? Map(SrlStatus status) => status switch
    {
        SrlStatus.Idle => RecorderStatus.Idle,
        SrlStatus.Recording => RecorderStatus.Recording,
        SrlStatus.Finishing => RecorderStatus.Finalizing,
        // We never call Pause(); if the library ever reports it, surface it as still-recording.
        SrlStatus.Paused => RecorderStatus.Recording,
        _ => null,
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        // A dispose while still running is a stop.
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Dispose must not throw.
        }

        _recorder.StatusChanged -= OnStatusChanged;
        _recorder.FrameRecorded -= OnFrameRecorded;
        _recorder.Completed -= OnCompleted;
        _recorder.Failed -= OnFailed;
        _recorder.Dispose();
    }
}
