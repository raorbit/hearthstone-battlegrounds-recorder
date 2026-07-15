namespace BgRecorder.Core.Audio;

/// <summary>
/// Audio capture to a staging WAV. Game-only process loopback on Windows 11 (build 20348+),
/// full system loopback as the honest Windows 10 fallback. Mic mixing is settings-driven.
/// </summary>
public interface IAudioCapture
{
    Task<IAudioSession> StartAsync(AudioTarget target, string stagingWavPath, CancellationToken ct);
}

public interface IAudioSession : IAsyncDisposable
{
    /// <summary>Wall clock of the first captured sample, once known. Used for the A/V mux offset.</summary>
    DateTimeOffset? FirstSampleWallClock { get; }

    /// <summary>
    /// The capture mode that actually ran. Differs from the requested mode when game-only
    /// process loopback was unavailable (older OS build or activation failure) and the engine
    /// fell back to full system loopback. The caller compares this against the requested mode
    /// to warn the user that non-game audio may be captured.
    /// </summary>
    AudioCaptureMode ActualMode { get; }

    /// <summary>
    /// Raised when a captured stream fails mid-recording, tagged with which stream failed. A
    /// <see cref="AudioStreamKind.Microphone"/> failure leaves the game audio intact, so the caller
    /// should keep the session alive and still finalize it; a <see cref="AudioStreamKind.Game"/>
    /// failure compromises the recording's audio.
    /// </summary>
    event Action<AudioFailure>? Failed;

    Task<AudioResult> StopAsync();
}

/// <summary>Which captured stream a failure came from.</summary>
public enum AudioStreamKind
{
    /// <summary>The game audio stream (process or system loopback).</summary>
    Game = 0,

    /// <summary>The optional microphone stream mixed into the game audio.</summary>
    Microphone = 1,
}

/// <summary>A mid-recording capture failure raised by <see cref="IAudioSession"/>, tagged with its source stream.</summary>
public sealed record AudioFailure(AudioStreamKind Source, string Message);

public sealed record AudioTarget(int ProcessId, bool IncludeProcessTree, AudioCaptureMode Mode)
{
    public bool MixMicrophone { get; init; }
}

public enum AudioCaptureMode
{
    /// <summary>Game audio only (AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK); requires build 20348+.</summary>
    ProcessLoopback = 0,

    /// <summary>All system output (standard WASAPI loopback); the Windows 10 fallback.</summary>
    SystemLoopback = 1,
}

public sealed record AudioResult(string Path, TimeSpan Duration);

/// <summary>Muxes the staged video and audio into the final library MP4 (video remuxed, audio encoded to AAC).</summary>
public interface IMuxer
{
    /// <param name="audioWav">Staged audio WAV; an empty string means no audio — the video is remuxed alone.</param>
    /// <param name="audioOffset">Positive = audio started after video's first frame; shifts audio right.</param>
    Task MuxAsync(string videoMp4, string audioWav, TimeSpan audioOffset, string outputMp4, CancellationToken ct);
}
