using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BgRecorder.Audio.Capture;

/// <summary>
/// NAudio-backed capture used for two paths that share identical plumbing:
/// full system-output loopback (the honest Windows 10 fallback) and default
/// microphone capture (for mic mixing). Writes the device's native mix format;
/// downstream mixing/muxing resamples to 44.1 kHz / 16-bit / stereo as needed.
/// Device loss surfaces through <see cref="Failed"/> and leaves the partial WAV intact.
/// </summary>
internal sealed class WasapiWavRecorder : IWavRecorder
{
    private readonly IWaveIn _capture;
    private readonly string _outPath;
    private WaveFileWriter? _writer;
    private readonly ManualResetEventSlim _stopped = new(false);
    private long _dataBytes;
    private long _avgBytesPerSec;
    private long _firstSampleTicks; // UTC ticks of the first delivered sample; 0 = not yet seen.
    private int _failedRaised;
    private int _started;
    private int _disposed;

    private WasapiWavRecorder(IWaveIn capture, string outPath)
    {
        _capture = capture;
        _outPath = outPath;
    }

    /// <summary>Full system-output loopback on the default render device.</summary>
    public static WasapiWavRecorder ForSystemLoopback(string outPath)
        => new(new WasapiLoopbackCapture(), outPath);

    /// <summary>Default microphone / input device.</summary>
    public static WasapiWavRecorder ForMicrophone(string outPath)
        => new(new WasapiCapture(), outPath);

    public string OutputPath => _outPath;

    public DateTimeOffset? FirstSampleWallClock
    {
        get
        {
            // The capture thread publishes this once via Interlocked; read it atomically
            // so a caller on another thread never sees a torn DateTimeOffset value.
            long ticks = Interlocked.Read(ref _firstSampleTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public event Action<string>? Failed;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        WaveFormat format = _capture.WaveFormat;
        _avgBytesPerSec = format.AverageBytesPerSecond;
        _writer = new WaveFileWriter(_outPath, format);

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
    }

    public TimeSpan Stop()
    {
        try
        {
            _capture.StopRecording();
            // RecordingStopped fires asynchronously; wait so the writer is flushed.
            _stopped.Wait(TimeSpan.FromSeconds(5));
        }
        catch { /* already stopped by a device-loss event */ }

        long avg = _avgBytesPerSec > 0 ? _avgBytesPerSec : 1;
        return TimeSpan.FromSeconds(_dataBytes / (double)avg);
    }

    public void Dispose()
    {
        // Idempotent: AudioCaptureSession disposes recorders on stop (to free the WAV
        // handle before mixing) and again from DisposeAsync.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { _capture.StopRecording(); } catch { /* ignore */ }
        _stopped.Wait(TimeSpan.FromSeconds(2));
        _capture.Dispose();
        _writer?.Dispose();
        _stopped.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _writer is null)
            return;
        // Publish the first-sample wall clock exactly once, atomically (0 is the sentinel).
        if (Interlocked.Read(ref _firstSampleTicks) == 0)
            Interlocked.CompareExchange(ref _firstSampleTicks, DateTimeOffset.UtcNow.UtcTicks, 0);
        _writer.Write(e.Buffer, 0, e.BytesRecorded);
        _dataBytes += e.BytesRecorded;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        try
        {
            _writer?.Flush();
        }
        catch { /* best effort flush */ }

        if (e.Exception is not null && Interlocked.Exchange(ref _failedRaised, 1) == 0)
            Failed?.Invoke($"Audio device capture stopped: {e.Exception.Message}");

        _stopped.Set();
    }
}
