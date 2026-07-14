namespace BgRecorder.Capture.Internal;

/// <summary>Library recording status, mirrored from ScreenRecorderLib so the seam carries no SRL types.</summary>
internal enum SrlStatus
{
    Idle = 0,
    Recording = 1,
    Paused = 2,
    Finishing = 3,
}

/// <summary>
/// The slice of ScreenRecorderLib's concrete <c>Recorder</c> that <see cref="RecordingSessionImpl"/>
/// depends on, expressed with neutral payloads. This is the test seam: production uses
/// <see cref="SrlRecorderAdapter"/> over the real library; tests use a fake that raises these events
/// on demand, so the wrapper's state machine, first-frame stamping, and duration math are exercised
/// without loading the native capture DLL.
/// </summary>
internal interface ISrlRecorder : IDisposable
{
    /// <summary>Library status transitions (Idle → Recording → Finishing).</summary>
    event Action<SrlStatus>? StatusChanged;

    /// <summary>Fires once the container is finalized; carries the written file path.</summary>
    event Action<string>? Completed;

    /// <summary>Fires on recording failure; carries the error message.</summary>
    event Action<string>? Failed;

    /// <summary>Fires per encoded frame (frame number, media timestamp in 100 ns ticks).</summary>
    event Action<int, long>? FrameRecorded;

    /// <summary>Begin recording to the given path.</summary>
    void Record(string path);

    /// <summary>Request a graceful stop; the library finalizes and then raises <see cref="Completed"/>.</summary>
    void Stop();

    /// <summary>
    /// Best-effort: turn off per-frame preview once the first frame has been stamped, so the
    /// bulk of the recording carries no preview overhead. No-op if the library rejects the update.
    /// </summary>
    void TryDisableFramePreview();
}
