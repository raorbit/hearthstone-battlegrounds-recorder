using BgRecorder.Audio;
using BgRecorder.Audio.Capture;
using BgRecorder.Core.Audio;
using NAudio.Wave;

namespace BgRecorder.Audio.Tests;

/// <summary>
/// Regression coverage for the mic-mixing sharing-violation bug: <see cref="AudioCaptureSession"/>
/// must release the game/mic recorder file handles before <see cref="Mixing.WavMixer.Mix"/> opens
/// the same WAVs for reading. Uses fake recorders (no real audio device) whose lifetime mimics
/// NAudio's <c>WaveFileWriter</c> — a valid PCM WAV is written to disk, but the file handle stays
/// open (write access, FileShare.Read) until Dispose. Before the fix the reader hit a sharing
/// violation, mixing fell onto the salvage path, and the mic staging WAV leaked.
/// </summary>
public sealed class AudioCaptureSessionMixingTests : IDisposable
{
    private readonly string _dir;

    public AudioCaptureSessionMixingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bg-audiosession-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task StopAsync_WithMic_ReleasesHandlesAndProducesAGenuineMix()
    {
        // Mirror AudioCaptureEngine's staging layout: game/mic land in pre-mix files and the
        // mixed result takes the caller's requested (final) path.
        string finalPath = Path.Combine(_dir, "session.wav");
        string gamePath = finalPath + ".game.wav";
        string micPath = finalPath + ".mic.wav";

        // Deliberately different rates/channels + amplitudes: the mixed output is always
        // canonical 44.1 kHz stereo, while a salvaged game WAV would keep the game's 48 kHz.
        const short gameAmplitude = 5000;
        const short micAmplitude = 6000;
        var game = new HandleHoldingWavRecorder(gamePath, new WaveFormat(48000, 16, 2), gameAmplitude, seconds: 1.0);
        var mic = new HandleHoldingWavRecorder(micPath, new WaveFormat(44100, 16, 1), micAmplitude, seconds: 1.0);
        game.Start();
        mic.Start();

        string? failure = null;
        await using (var session = new AudioCaptureSession(
            game, mic, finalPath, gamePath, micPath, AudioCaptureMode.SystemLoopback))
        {
            session.Failed += f => failure = f.Message;

            AudioResult result = await session.StopAsync();

            // (a) The mix succeeded — no fallback to salvage, so Failed was never raised.
            Assert.Null(failure);
            Assert.Equal(finalPath, result.Path);
            Assert.True(result.Duration > TimeSpan.FromMilliseconds(800),
                $"expected the game duration (~1s), got {result.Duration}");

            // (b) The final path is a genuinely MIXED WAV, not the salvaged 48 kHz game WAV:
            //     canonical 44.1 kHz / stereo / 16-bit AND a peak that exceeds either source
            //     alone (both were summed).
            Assert.True(File.Exists(finalPath), "final mixed WAV must exist");
            using (var reader = new WaveFileReader(finalPath))
            {
                Assert.Equal(44100, reader.WaveFormat.SampleRate);
                Assert.Equal(2, reader.WaveFormat.Channels);
                Assert.Equal(16, reader.WaveFormat.BitsPerSample);
            }
            int peak = PeakAbsSample(finalPath);
            Assert.True(peak > micAmplitude,
                $"mixed peak {peak} should exceed the louder input ({micAmplitude}); a salvaged game-only WAV would peak near {gameAmplitude}");

            // (c) The pre-mix staging WAVs were cleaned up after a successful mix.
            Assert.False(File.Exists(gamePath), "game staging WAV should be deleted after mixing");
            Assert.False(File.Exists(micPath), "mic staging WAV should be deleted after mixing (it leaked before the fix)");
        }
    }

    [Fact]
    public async Task StopAsync_WithoutMic_ReleasesGameHandleBeforeReturning()
    {
        // System-loopback-only (no mic): the game stream writes straight to the final path, and
        // StopAsync must dispose the recorder so its file handle is released before the muxer opens
        // the WAV. Before the fix the handle stayed open, Media Foundation rejected the byte stream,
        // and the committed VOD was silent.
        string finalPath = Path.Combine(_dir, "session.wav");
        var game = new HandleHoldingWavRecorder(finalPath, new WaveFormat(48000, 16, 2), amplitude: 5000, seconds: 1.0);
        game.Start();

        await using (var session = new AudioCaptureSession(
            game, mic: null, finalPath, finalPath, micPath: null, AudioCaptureMode.SystemLoopback))
        {
            AudioResult result = await session.StopAsync();

            Assert.Equal(finalPath, result.Path);
            // Exclusive open succeeds only if no writer handle remains — the proxy for "Media
            // Foundation can now open this file".
            using var probe = new FileStream(finalPath, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.True(probe.Length > 0);
        }
    }

    [Fact]
    public async Task StopAsync_WhenMicRequestedButAbsent_SalvagesGameAudioToFinalPath()
    {
        // Mic mixing was requested but the mic failed to start, so the session has no mic yet the
        // game stream wrote to a pre-mix staging path. StopAsync must move that game WAV to the
        // final path (and release its handle), or the muxer finds no audio and yields a silent VOD.
        string finalPath = Path.Combine(_dir, "session.wav");
        string gamePath = finalPath + ".game.wav";
        var game = new HandleHoldingWavRecorder(gamePath, new WaveFormat(48000, 16, 2), amplitude: 5000, seconds: 1.0);
        game.Start();

        await using (var session = new AudioCaptureSession(
            game, mic: null, finalPath, gamePath, micPath: null, AudioCaptureMode.SystemLoopback))
        {
            AudioResult result = await session.StopAsync();

            Assert.Equal(finalPath, result.Path);
            Assert.True(File.Exists(finalPath), "game audio must be salvaged to the final path");
            Assert.False(File.Exists(gamePath), "the pre-mix game WAV should have been moved");
            using var probe = new FileStream(finalPath, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.True(probe.Length > 0);
        }
    }

    [Fact]
    public async Task StopAsync_WhenMixGenuinelyFails_SalvagesGameAudioAndReportsFailure()
    {
        string finalPath = Path.Combine(_dir, "session.wav");
        string gamePath = finalPath + ".game.wav";
        string micPath = finalPath + ".mic.wav";

        // A real, recognizable game WAV (48 kHz) plus a mic staging file that is not valid audio,
        // so WavMixer.Mix genuinely throws and the session must fall back to salvaging the game WAV.
        var game = new HandleHoldingWavRecorder(gamePath, new WaveFormat(48000, 16, 2), amplitude: 5000, seconds: 1.0);
        var mic = new BrokenWavRecorder(micPath);
        game.Start();
        mic.Start();

        string? failure = null;
        await using (var session = new AudioCaptureSession(
            game, mic, finalPath, gamePath, micPath, AudioCaptureMode.SystemLoopback))
        {
            session.Failed += f => failure = f.Message;

            AudioResult result = await session.StopAsync();

            // Failure is surfaced, but StopAsync still returns the game duration and a usable path.
            Assert.NotNull(failure);
            Assert.Equal(finalPath, result.Path);
            Assert.True(result.Duration > TimeSpan.FromMilliseconds(800),
                $"expected the game duration (~1s), got {result.Duration}");

            // The salvaged file is the game WAV verbatim (48 kHz), NOT a canonical 44.1 kHz mix.
            Assert.True(File.Exists(finalPath), "salvaged game WAV must exist at the final path");
            using var reader = new WaveFileReader(finalPath);
            Assert.Equal(48000, reader.WaveFormat.SampleRate);
            Assert.False(File.Exists(gamePath), "game WAV should have been moved to the final path");
        }
    }

    private static int PeakAbsSample(string path)
    {
        using var reader = new WaveFileReader(path);
        var buffer = new byte[reader.WaveFormat.AverageBytesPerSecond];
        int peak = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i + 1 < read; i += 2)
            {
                short s = (short)(buffer[i] | (buffer[i + 1] << 8));
                int abs = Math.Abs((int)s);
                if (abs > peak) peak = abs;
            }
        }
        return peak;
    }

    /// <summary>
    /// A fake recorder that writes a real constant-amplitude PCM WAV, then keeps the file handle
    /// open (via NAudio's WaveFileWriter, which holds it with write access + FileShare.Read) until
    /// Dispose — exactly the lifetime that caused the mixer's reader to fail with a sharing
    /// violation. Stop() flushes but does not release; only Dispose() frees the handle.
    /// </summary>
    private sealed class HandleHoldingWavRecorder : IWavRecorder
    {
        private readonly WaveFormat _format;
        private readonly short _amplitude;
        private readonly double _seconds;
        private WaveFileWriter? _writer;
        private int _disposed;

        public HandleHoldingWavRecorder(string outPath, WaveFormat format, short amplitude, double seconds)
        {
            OutputPath = outPath;
            _format = format;
            _amplitude = amplitude;
            _seconds = seconds;
        }

        public string OutputPath { get; }
        public DateTimeOffset? FirstSampleWallClock { get; private set; }

        // The fake never fails; a no-op accessor keeps the interface contract without CS0067.
        public event Action<string>? Failed { add { } remove { } }

        public void Start()
        {
            _writer = new WaveFileWriter(OutputPath, _format);
            int bytesPerSample = _format.BitsPerSample / 8;
            var frame = new byte[_format.Channels * bytesPerSample];
            for (int c = 0; c < _format.Channels; c++)
            {
                frame[c * bytesPerSample] = (byte)(_amplitude & 0xFF);
                frame[c * bytesPerSample + 1] = (byte)((_amplitude >> 8) & 0xFF);
            }

            FirstSampleWallClock = DateTimeOffset.UtcNow;
            int frames = (int)(_format.SampleRate * _seconds);
            for (int i = 0; i < frames; i++)
                _writer.Write(frame, 0, frame.Length);

            // Flush the header (valid, readable once the handle is freed) but keep the file open,
            // just like WasapiWavRecorder's writer before Stop disposes it.
            _writer.Flush();
        }

        public TimeSpan Stop() => TimeSpan.FromSeconds(_seconds);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>
    /// Writes a file that is NOT a valid WAV so the mixer's reader throws, exercising the
    /// genuine mix-failure / salvage path. Holds no lingering handle.
    /// </summary>
    private sealed class BrokenWavRecorder : IWavRecorder
    {
        public BrokenWavRecorder(string outPath) => OutputPath = outPath;

        public string OutputPath { get; }
        public DateTimeOffset? FirstSampleWallClock { get; private set; }
        public event Action<string>? Failed { add { } remove { } }

        public void Start()
        {
            FirstSampleWallClock = DateTimeOffset.UtcNow;
            File.WriteAllBytes(OutputPath, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 });
        }

        public TimeSpan Stop() => TimeSpan.FromSeconds(1.0);

        public void Dispose() { }
    }
}
