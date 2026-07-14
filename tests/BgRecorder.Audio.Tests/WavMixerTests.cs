using BgRecorder.Audio.Mixing;
using NAudio.Wave;

namespace BgRecorder.Audio.Tests;

public class WavMixerTests : IDisposable
{
    private readonly string _dir;

    public WavMixerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bg-wavmixer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Mix_ProducesCanonical44kStereo16BitOutput()
    {
        // Inputs at deliberately different rates/channels to exercise resample + upmix.
        string game = WriteConstantPcm16(Path.Combine(_dir, "game.wav"), 48000, 2, amplitude: 4000, seconds: 1.0);
        string mic = WriteConstantPcm16(Path.Combine(_dir, "mic.wav"), 16000, 1, amplitude: 3000, seconds: 1.0);
        string outPath = Path.Combine(_dir, "mix.wav");

        WavMixer.Mix(game, mic, outPath);

        using var reader = new WaveFileReader(outPath);
        Assert.Equal(44100, reader.WaveFormat.SampleRate);
        Assert.Equal(2, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.True(reader.TotalTime > TimeSpan.FromMilliseconds(800), $"expected ~1s of audio, got {reader.TotalTime}");
    }

    [Fact]
    public void Mix_SumsBothSourcesAboveEitherAlone()
    {
        string game = WriteConstantPcm16(Path.Combine(_dir, "game.wav"), 44100, 2, amplitude: 5000, seconds: 0.5);
        string mic = WriteConstantPcm16(Path.Combine(_dir, "mic.wav"), 44100, 2, amplitude: 6000, seconds: 0.5);
        string outPath = Path.Combine(_dir, "mix.wav");

        WavMixer.Mix(game, mic, outPath);

        int peak = PeakAbsSample(outPath);
        // Constant DC-ish tones sum toward ~11000; must exceed either input and stay in range.
        Assert.True(peak > 6000, $"mix peak {peak} should exceed the louder input (6000)");
        Assert.True(peak <= short.MaxValue);
    }

    [Fact]
    public void Mix_GuardsClippingWhenBothSourcesAreLoud()
    {
        string game = WriteConstantPcm16(Path.Combine(_dir, "game.wav"), 44100, 2, amplitude: 30000, seconds: 0.5);
        string mic = WriteConstantPcm16(Path.Combine(_dir, "mic.wav"), 44100, 2, amplitude: 30000, seconds: 0.5);
        string outPath = Path.Combine(_dir, "mix.wav");

        WavMixer.Mix(game, mic, outPath);

        int peak = PeakAbsSample(outPath);
        // 30000 + 30000 = 60000 must clamp to the 16-bit ceiling, never wrap around.
        Assert.True(peak >= 32000, $"expected clipping near ceiling, got {peak}");
        Assert.True(peak <= short.MaxValue);
    }

    private static string WriteConstantPcm16(string path, int sampleRate, int channels, short amplitude, double seconds)
    {
        var format = new WaveFormat(sampleRate, 16, channels);
        using var writer = new WaveFileWriter(path, format);
        int frames = (int)(sampleRate * seconds);
        var frame = new byte[channels * 2];
        for (int c = 0; c < channels; c++)
        {
            frame[c * 2] = (byte)(amplitude & 0xFF);
            frame[c * 2 + 1] = (byte)((amplitude >> 8) & 0xFF);
        }
        for (int i = 0; i < frames; i++)
            writer.Write(frame, 0, frame.Length);
        return path;
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
}
