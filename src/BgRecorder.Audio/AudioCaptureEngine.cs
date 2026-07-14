using BgRecorder.Audio.Capture;
using BgRecorder.Core.Audio;

namespace BgRecorder.Audio;

/// <summary>
/// <see cref="IAudioCapture"/> implementation. Honours <see cref="AudioCaptureMode"/>
/// with an automatic downgrade: game-only process loopback needs Windows build 20348+
/// and a successful activation; on an older OS or an activation failure it falls back to
/// full system-output loopback. The mode that actually ran is exposed on the returned
/// <see cref="AudioCaptureSession"/> (<see cref="AudioCaptureSession.ActualMode"/>) so the
/// caller can label the recording honestly. Mic mixing captures the default input device
/// concurrently and mixes on stop.
/// </summary>
public sealed class AudioCaptureEngine : IAudioCapture
{
    /// <summary>Minimum Windows build for AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK.</summary>
    public const int ProcessLoopbackMinBuild = 20348;

    private readonly int _osBuild;

    public AudioCaptureEngine()
        : this(Environment.OSVersion.Version.Build)
    {
    }

    // Test seam: pretend to run on an older/newer OS build.
    internal AudioCaptureEngine(int osBuild) => _osBuild = osBuild;

    public Task<IAudioSession> StartAsync(AudioTarget target, string stagingWavPath, CancellationToken ct)
        => Task.Run<IAudioSession>(() => StartInternal(target, stagingWavPath, ct), ct);

    private AudioCaptureSession StartInternal(AudioTarget target, string stagingWavPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(stagingWavPath))!);

        bool mixMic = target.MixMicrophone;
        // With mic mixing, game and mic land in pre-mix staging files; the mixed result
        // takes the caller's requested path. Without mixing, the game stream writes there directly.
        string gamePath = mixMic ? stagingWavPath + ".game.wav" : stagingWavPath;
        string? micPath = mixMic ? stagingWavPath + ".mic.wav" : null;

        var (gameRecorder, actualMode) = StartGameRecorder(target, gamePath, ct);

        IWavRecorder? micRecorder = null;
        if (mixMic)
        {
            try
            {
                micRecorder = WasapiWavRecorder.ForMicrophone(micPath!);
                micRecorder.Start();
            }
            catch
            {
                // A missing/blocked mic must not sink the recording; drop mic and carry on
                // with game audio only. The session reports the degraded mixing via Failed.
                micRecorder?.Dispose();
                micRecorder = null;
            }
        }

        return new AudioCaptureSession(gameRecorder, micRecorder, stagingWavPath, gamePath, micPath, actualMode);
    }

    private (IWavRecorder recorder, AudioCaptureMode mode) StartGameRecorder(AudioTarget target, string gamePath, CancellationToken ct)
    {
        bool wantProcessLoopback = target.Mode == AudioCaptureMode.ProcessLoopback;
        bool processLoopbackAvailable = wantProcessLoopback && _osBuild >= ProcessLoopbackMinBuild;

        if (processLoopbackAvailable)
        {
            var recorder = new ProcessLoopbackRecorder((uint)target.ProcessId, target.IncludeProcessTree, gamePath);
            try
            {
                recorder.Start();
                return (recorder, AudioCaptureMode.ProcessLoopback);
            }
            catch
            {
                // Activation failed (e.g. process exited, policy) — fall back to system loopback.
                recorder.Dispose();
                TryDelete(gamePath);
            }
        }

        var system = WasapiWavRecorder.ForSystemLoopback(gamePath);
        system.Start();
        return (system, AudioCaptureMode.SystemLoopback);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
