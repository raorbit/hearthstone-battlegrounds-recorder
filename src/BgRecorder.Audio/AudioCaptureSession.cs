using BgRecorder.Audio.Capture;
using BgRecorder.Audio.Mixing;
using BgRecorder.Core.Audio;

namespace BgRecorder.Audio;

/// <summary>
/// A live audio capture: one game stream plus an optional concurrent mic stream.
/// On <see cref="StopAsync"/> the streams are stopped, mixed if a mic was captured,
/// and a single staging WAV is returned. Device loss on either stream is re-raised
/// through <see cref="Failed"/> but never prevents <see cref="StopAsync"/> from
/// returning whatever audio was captured.
/// </summary>
public sealed class AudioCaptureSession : IAudioSession
{
    private readonly IWavRecorder _game;
    private readonly IWavRecorder? _mic;
    private readonly string _finalPath;
    private readonly string _gamePath;
    private readonly string? _micPath;
    private int _stopped;

    internal AudioCaptureSession(
        IWavRecorder game,
        IWavRecorder? mic,
        string finalPath,
        string gamePath,
        string? micPath,
        AudioCaptureMode actualMode)
    {
        _game = game;
        _mic = mic;
        _finalPath = finalPath;
        _gamePath = gamePath;
        _micPath = micPath;
        ActualMode = actualMode;

        _game.Failed += OnStreamFailed;
        if (_mic is not null)
            _mic.Failed += OnStreamFailed;
    }

    /// <summary>
    /// The capture mode that actually ran. May differ from the requested mode when
    /// process loopback was unavailable and the engine fell back to system loopback;
    /// the caller uses this to label the recording honestly. Not surfaced on
    /// <see cref="IAudioSession"/> because <see cref="AudioResult"/> carries only a path
    /// and duration.
    /// </summary>
    public AudioCaptureMode ActualMode { get; }

    public DateTimeOffset? FirstSampleWallClock => _game.FirstSampleWallClock;

    public event Action<string>? Failed;

    public Task<AudioResult> StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            throw new InvalidOperationException("StopAsync has already been called.");

        return Task.Run(() =>
        {
            TimeSpan gameDuration = _game.Stop();

            if (_mic is null)
                return new AudioResult(_finalPath, gameDuration);

            _mic.Stop();

            // Mix mic into game audio. If mixing fails for any reason, fall back to the
            // raw game WAV so the recording still yields usable audio.
            try
            {
                WavMixer.Mix(_gamePath, _micPath!, _finalPath);
                TryDelete(_gamePath);
                TryDelete(_micPath!);
                return new AudioResult(_finalPath, gameDuration);
            }
            catch (Exception ex)
            {
                RaiseFailed($"Mic mixing failed, keeping game audio only: {ex.Message}");
                SalvageGameAudio();
                return new AudioResult(_finalPath, gameDuration);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _stopped, 1, 0) == 0)
        {
            // Disposed without a graceful stop: shut the streams down so files are flushed.
            await Task.Run(() =>
            {
                try { _game.Stop(); } catch { /* ignore */ }
                try { _mic?.Stop(); } catch { /* ignore */ }
            }).ConfigureAwait(false);
        }

        _game.Dispose();
        _mic?.Dispose();
    }

    private void SalvageGameAudio()
    {
        // Ensure the final path holds the game WAV when mixing could not produce one.
        try
        {
            if (!string.Equals(_gamePath, _finalPath, StringComparison.OrdinalIgnoreCase) && File.Exists(_gamePath))
            {
                if (File.Exists(_finalPath))
                    File.Delete(_finalPath);
                File.Move(_gamePath, _finalPath);
            }
        }
        catch { /* best effort salvage */ }
    }

    private void OnStreamFailed(string message) => RaiseFailed(message);

    private void RaiseFailed(string message) => Failed?.Invoke(message);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
