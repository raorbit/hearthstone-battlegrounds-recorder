using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SpikeD.ProcessAudio;

/// <summary>
/// Windows-10 fallback path: full system-output loopback via NAudio's
/// WasapiLoopbackCapture (standard WASAPI, available on every supported build).
/// Captures EVERYTHING the default render device plays, not just the game —
/// this is the honest degradation when process loopback is unavailable.
/// </summary>
internal static class SystemLoopbackCapture
{
    public static CaptureResult Capture(int seconds, string outPath)
    {
        using var capture = new WasapiLoopbackCapture(); // default render device
        WaveFormat format = capture.WaveFormat;

        double peak = 0;
        double sumSquares = 0;
        long sampleCount = 0;
        long dataBytes = 0;
        Exception? failure = null;

        using var writer = new WaveFileWriter(outPath, format);
        using var finished = new ManualResetEventSlim(false);

        capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0)
                return;
            writer.Write(e.Buffer, 0, e.BytesRecorded);
            dataBytes += e.BytesRecorded;
            AccumulateLevels(format, e.Buffer, e.BytesRecorded, ref peak, ref sumSquares, ref sampleCount);
        };
        capture.RecordingStopped += (_, e) =>
        {
            failure = e.Exception;
            finished.Set();
        };

        capture.StartRecording();
        if (!finished.Wait(TimeSpan.FromSeconds(seconds)))
        {
            capture.StopRecording();
            finished.Wait(TimeSpan.FromSeconds(5));
        }

        writer.Flush();
        if (failure != null)
            throw failure;

        double durationSeconds = format.AverageBytesPerSecond > 0
            ? dataBytes / (double)format.AverageBytesPerSecond
            : 0.0;
        double rms = sampleCount > 0 ? Math.Sqrt(sumSquares / sampleCount) : 0.0;

        return new CaptureResult(
            ToDb(peak), ToDb(rms), peak, rms,
            durationSeconds, format.SampleRate, format.Channels, format.BitsPerSample,
            dataBytes, peak == 0);
    }

    // WasapiLoopbackCapture delivers the device mix format, which is almost always
    // 32-bit IEEE float; handle both float and 16-bit PCM defensively.
    private static void AccumulateLevels(WaveFormat format, byte[] buffer, int byteCount, ref double peak, ref double sumSquares, ref long sampleCount)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (int i = 0; i + 3 < byteCount; i += 4)
            {
                float sample = BitConverter.ToSingle(buffer, i);
                double abs = Math.Abs(sample);
                if (abs > peak)
                    peak = abs;
                sumSquares += (double)sample * sample;
                sampleCount++;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (int i = 0; i + 1 < byteCount; i += 2)
            {
                short raw = (short)(buffer[i] | (buffer[i + 1] << 8));
                double sample = raw / 32768.0;
                double abs = Math.Abs(sample);
                if (abs > peak)
                    peak = abs;
                sumSquares += sample * sample;
                sampleCount++;
            }
        }
    }

    private static double ToDb(double normalized)
        => normalized <= 0 ? double.NegativeInfinity : 20.0 * Math.Log10(normalized);
}
