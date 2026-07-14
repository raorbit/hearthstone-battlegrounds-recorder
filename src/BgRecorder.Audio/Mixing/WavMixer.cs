using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BgRecorder.Audio.Mixing;

/// <summary>
/// Mixes a game-audio WAV and a microphone WAV into a single 44.1 kHz / 16-bit /
/// stereo WAV. Both inputs are resampled and channel-normalized to that format,
/// then summed at unity gain through <see cref="PcmMath.MixSample"/> so clipping is
/// guarded. Streams block-by-block rather than buffering whole files, so a 30-minute
/// recording does not need the entire PCM in memory.
/// </summary>
public static class WavMixer
{
    public const int TargetSampleRate = 44100;
    public const int TargetChannels = 2;
    public const int TargetBitsPerSample = 16;

    public static void Mix(string gameWavPath, string micWavPath, string outputWavPath)
    {
        using var gameReader = new AudioFileReader(gameWavPath);
        using var micReader = new AudioFileReader(micWavPath);

        IWaveProvider game = ToPcm16Stereo44k(gameReader);
        IWaveProvider mic = ToPcm16Stereo44k(micReader);

        var outFormat = new WaveFormat(TargetSampleRate, TargetBitsPerSample, TargetChannels);
        using var writer = new WaveFileWriter(outputWavPath, outFormat);

        // 0.1 s of stereo 16-bit PCM per block.
        const int blockBytes = TargetSampleRate * TargetChannels * (TargetBitsPerSample / 8) / 10;
        var gameBuffer = new byte[blockBytes];
        var micBuffer = new byte[blockBytes];
        var outBuffer = new byte[blockBytes];

        while (true)
        {
            int gn = ReadFull(game, gameBuffer);
            int mn = ReadFull(mic, micBuffer);
            int n = Math.Max(gn, mn);
            if (n == 0)
                break;

            for (int i = gn; i < n; i++) gameBuffer[i] = 0;
            for (int i = mn; i < n; i++) micBuffer[i] = 0;

            for (int i = 0; i + 1 < n; i += 2)
            {
                short g = (short)(gameBuffer[i] | (gameBuffer[i + 1] << 8));
                short m = (short)(micBuffer[i] | (micBuffer[i + 1] << 8));
                short s = PcmMath.MixSample(g, m);
                outBuffer[i] = (byte)(s & 0xFF);
                outBuffer[i + 1] = (byte)((s >> 8) & 0xFF);
            }

            writer.Write(outBuffer, 0, n);
        }
    }

    private static IWaveProvider ToPcm16Stereo44k(AudioFileReader reader)
    {
        ISampleProvider sp = reader;
        // Mic and game sources are mono or stereo; upmix mono to stereo, then resample.
        if (sp.WaveFormat.Channels == 1)
            sp = new MonoToStereoSampleProvider(sp);

        if (sp.WaveFormat.SampleRate != TargetSampleRate)
            sp = new WdlResamplingSampleProvider(sp, TargetSampleRate);

        return new SampleToWaveProvider16(sp);
    }

    private static int ReadFull(IWaveProvider provider, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = provider.Read(buffer, total, buffer.Length - total);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }
}
