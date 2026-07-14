using BgRecorder.Capture.Internal;

namespace BgRecorder.Capture.Tests;

/// <summary>
/// Test double for <see cref="ISrlRecorder"/>. Records the calls the session makes and lets a test
/// drive the library-side callbacks (status, frame, complete, fail) with explicit timing, so the
/// wrapper's state machine is exercised without the native ScreenRecorderLib.
/// </summary>
internal sealed class FakeSrlRecorder : ISrlRecorder
{
    public int RecordCount { get; private set; }
    public int StopCount { get; private set; }
    public int DisablePreviewCount { get; private set; }
    public string? LastRecordPath { get; private set; }
    public bool Disposed { get; private set; }

    public event Action<SrlStatus>? StatusChanged;
    public event Action<string>? Completed;
    public event Action<string>? Failed;
    public event Action<int, long>? FrameRecorded;

    public void Record(string path)
    {
        RecordCount++;
        LastRecordPath = path;
    }

    public void Stop() => StopCount++;

    public void TryDisableFramePreview() => DisablePreviewCount++;

    public void Dispose() => Disposed = true;

    // --- test drivers ---
    public void RaiseStatus(SrlStatus status) => StatusChanged?.Invoke(status);
    public void RaiseFrame(int frameNumber = 0, long timestamp = 0) => FrameRecorded?.Invoke(frameNumber, timestamp);
    public void RaiseCompleted(string path) => Completed?.Invoke(path);
    public void RaiseFailed(string error) => Failed?.Invoke(error);
}
