namespace BgRecorder.Core.Session;

/// <summary>
/// The recording state machine: Idle → Armed → Recording → Finalizing → Armed.
/// Drives the three tray/status states the prototype shows and the two distinct user controls.
/// </summary>
public interface ISessionCoordinator : IAsyncDisposable
{
    CoordinatorState State { get; }

    event Action<CoordinatorState>? StateChanged;

    /// <summary>Human-readable operational warning or degradation detail.</summary>
    event Action<string>? Diagnostic;

    Task StartAsync(CancellationToken ct);

    /// <summary>"Stop this recording": finalize the current match early, stay armed for the next one.</summary>
    Task StopCurrentRecordingAsync();

    /// <summary>"Pause auto-recording": disarm. A current recording, if any, still finishes.</summary>
    void PauseAutoRecording();

    /// <summary>Re-arm now (the resume affordance's immediate option).</summary>
    void ResumeNow();
}

public enum CoordinatorState
{
    /// <summary>Hearthstone isn't running / no live log folder found.</summary>
    GameNotFound = 0,

    /// <summary>Waiting for a match to start.</summary>
    Armed = 1,

    Recording = 2,

    Finalizing = 3,

    /// <summary>Auto-recording paused by the user (distinct tray glyph).</summary>
    Paused = 4,

    /// <summary>
    /// Hearthstone is available, but the staging volume is below its safety floor (or free
    /// space could not be determined), so new recordings are disarmed until storage recovers.
    /// </summary>
    StorageBlocked = 5,
}

/// <summary>Disk-safety gate: floor check before arming, watchdog during recording.</summary>
public interface IDiskSafety
{
    /// <summary>Floor = max(10 GB, 2× rolling-average match size) on the staging volume.</summary>
    ArmCheckResult CheckCanArm();

    /// <summary>Poll free space while recording; callback fires once when below the floor → finalize early.</summary>
    IDisposable StartWatchdog(Action onLowSpace);
}

public sealed record ArmCheckResult(bool CanArm, string? Reason);
