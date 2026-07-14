using ScreenRecorderLib;
using SrlLibStatus = ScreenRecorderLib.RecorderStatus;

namespace BgRecorder.Capture.Internal;

/// <summary>
/// Production <see cref="ISrlRecorder"/> over the concrete ScreenRecorderLib <see cref="Recorder"/>.
/// Translates the library's typed event args into the seam's neutral events and owns the
/// dynamic-options path used to disable frame preview after the first frame.
/// </summary>
internal sealed class SrlRecorderAdapter : ISrlRecorder
{
    private readonly Recorder _recorder;
    private readonly WindowRecordingSource _source;
    private int _previewDisabled;

    public SrlRecorderAdapter(Recorder recorder, WindowRecordingSource source)
    {
        _recorder = recorder;
        _source = source;

        _recorder.OnStatusChanged += HandleStatus;
        _recorder.OnRecordingComplete += HandleComplete;
        _recorder.OnRecordingFailed += HandleFailed;
        _recorder.OnFrameRecorded += HandleFrame;
    }

    public event Action<SrlStatus>? StatusChanged;
    public event Action<string>? Completed;
    public event Action<string>? Failed;
    public event Action<int, long>? FrameRecorded;

    public void Record(string path) => _recorder.Record(path);

    public void Stop() => _recorder.Stop();

    public void TryDisableFramePreview()
    {
        // Run once, and never let a library quirk here take down a live recording.
        if (Interlocked.Exchange(ref _previewDisabled, 1) == 1)
            return;

        try
        {
            _source.IsVideoFramePreviewEnabled = false;
            _recorder.GetDynamicOptionsBuilder()
                     .SetUpdatedRecordingSource(_source)
                     .Apply();
        }
        catch
        {
            // Unsupported / rejected: preview stays on at its minimal size — a bounded cost, not a failure.
        }
    }

    private void HandleStatus(object? sender, RecordingStatusEventArgs e) =>
        StatusChanged?.Invoke(Map(e.Status));

    private void HandleComplete(object? sender, RecordingCompleteEventArgs e) =>
        Completed?.Invoke(e.FilePath);

    private void HandleFailed(object? sender, RecordingFailedEventArgs e) =>
        Failed?.Invoke(e.Error);

    private void HandleFrame(object? sender, FrameRecordedEventArgs e) =>
        FrameRecorded?.Invoke(e.FrameNumber, e.Timestamp);

    private static SrlStatus Map(SrlLibStatus status) => status switch
    {
        SrlLibStatus.Idle => SrlStatus.Idle,
        SrlLibStatus.Recording => SrlStatus.Recording,
        SrlLibStatus.Paused => SrlStatus.Paused,
        SrlLibStatus.Finishing => SrlStatus.Finishing,
        _ => SrlStatus.Idle,
    };

    public void Dispose()
    {
        _recorder.OnStatusChanged -= HandleStatus;
        _recorder.OnRecordingComplete -= HandleComplete;
        _recorder.OnRecordingFailed -= HandleFailed;
        _recorder.OnFrameRecorded -= HandleFrame;
        _recorder.Dispose();
    }
}
