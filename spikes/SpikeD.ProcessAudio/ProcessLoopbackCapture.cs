using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SpikeD.ProcessAudio;

/// <summary>
/// Captures audio from ONLY the target process (and optionally its child tree)
/// via AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK. Requires Windows build 20348+.
/// </summary>
internal static class ProcessLoopbackCapture
{
    // Fixed capture format (same choice as Microsoft's ApplicationLoopback sample).
    // The process-loopback virtual device resamples the game's native mix to this.
    private const int SampleRate = 44100;
    private const int Channels = 2;
    private const int BitsPerSample = 16;

    public static CaptureResult Capture(uint targetPid, bool captureEverythingExceptTarget, int seconds, string outPath)
    {
        // WASAPI process loopback has exactly two modes: capture the target's process tree, or
        // capture everything EXCEPT it. There is no "target without its children" mode, so capturing
        // the game (this spike's goal) must always Include the target tree. Selecting Exclude only
        // makes sense when the caller explicitly wants all OTHER audio — never as a "no child tree"
        // toggle: doing that captured every other app and then reported it as "game audio captured".
        var loopbackMode = captureEverythingExceptTarget
            ? ProcessLoopbackMode.ExcludeTargetProcessTree
            : ProcessLoopbackMode.IncludeTargetProcessTree;

        IAudioClient audioClient = ActivateProcessLoopbackClient(targetPid, loopbackMode);

        // --- Initialize the client in shared loopback + event-callback mode ---
        var format = new WaveFormatEx
        {
            wFormatTag = NativeAudio.WAVE_FORMAT_PCM,
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = BitsPerSample,
            nBlockAlign = (ushort)(Channels * BitsPerSample / 8),
            nAvgBytesPerSec = (uint)(SampleRate * Channels * BitsPerSample / 8),
            cbSize = 0,
        };

        int blockAlign = format.nBlockAlign;
        IntPtr pFormat = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        try
        {
            Marshal.StructureToPtr(format, pFormat, false);

            const long bufferDuration = 200000; // 20 ms in 100-ns REFERENCE_TIME units
            uint streamFlags = NativeAudio.AUDCLNT_STREAMFLAGS_LOOPBACK
                             | NativeAudio.AUDCLNT_STREAMFLAGS_EVENTCALLBACK;

            int hr = audioClient.Initialize(
                NativeAudio.AUDCLNT_SHAREMODE_SHARED, streamFlags,
                bufferDuration, 0, pFormat, IntPtr.Zero);
            Check(hr, "IAudioClient.Initialize");
        }
        finally
        {
            Marshal.FreeHGlobal(pFormat);
        }

        Guid captureIid = NativeAudio.IID_IAudioCaptureClient;
        Check(audioClient.GetService(ref captureIid, out object captureObj), "IAudioClient.GetService(IAudioCaptureClient)");
        var captureClient = (IAudioCaptureClient)captureObj;

        using var sampleReady = new AutoResetEvent(false);
        Check(audioClient.SetEventHandle(sampleReady.SafeWaitHandle.DangerousGetHandle()), "IAudioClient.SetEventHandle");

        var waveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels);
        using var writer = new WaveFileWriter(outPath, waveFormat);

        long peak = 0;          // max |sample| over 16-bit range
        double sumSquares = 0;  // for RMS
        long sampleCount = 0;   // individual 16-bit samples (channels counted separately)
        var scratch = new byte[format.nAvgBytesPerSec]; // ~1s headroom

        Check(audioClient.Start(), "IAudioClient.Start");
        var sw = Stopwatch.StartNew();
        try
        {
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                // Wake on buffer-ready; short timeout so we re-check elapsed time.
                sampleReady.WaitOne(50);

                while (true)
                {
                    Check(captureClient.GetNextPacketSize(out uint packetFrames), "GetNextPacketSize");
                    if (packetFrames == 0)
                        break;

                    Check(captureClient.GetBuffer(out IntPtr dataPtr, out uint frames, out uint flags, out _, out _), "GetBuffer");
                    int byteCount = (int)frames * blockAlign;

                    if (byteCount > 0)
                    {
                        if (byteCount > scratch.Length)
                            scratch = new byte[byteCount];

                        bool silent = (flags & NativeAudio.AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                        if (silent || dataPtr == IntPtr.Zero)
                        {
                            Array.Clear(scratch, 0, byteCount);
                        }
                        else
                        {
                            Marshal.Copy(dataPtr, scratch, 0, byteCount);
                            AccumulateLevels(scratch, byteCount, ref peak, ref sumSquares, ref sampleCount);
                        }

                        writer.Write(scratch, 0, byteCount);
                    }

                    Check(captureClient.ReleaseBuffer(frames), "ReleaseBuffer");
                }
            }
        }
        finally
        {
            audioClient.Stop();
            writer.Flush();
        }

        long dataBytes = writer.Length;
        double durationSeconds = dataBytes / (double)format.nAvgBytesPerSec;

        double peakNorm = peak / 32768.0;
        double rmsNorm = sampleCount > 0 ? Math.Sqrt(sumSquares / sampleCount) / 32768.0 : 0.0;

        return new CaptureResult(
            ToDb(peakNorm), ToDb(rmsNorm), peakNorm, rmsNorm,
            durationSeconds, SampleRate, Channels, BitsPerSample,
            dataBytes, peak == 0);
    }

    private static IAudioClient ActivateProcessLoopbackClient(uint targetPid, ProcessLoopbackMode mode)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams
            {
                TargetProcessId = targetPid,
                ProcessLoopbackMode = mode,
            },
        };

        int paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr pParams = Marshal.AllocHGlobal(paramsSize);
        IntPtr pPropVariant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(activationParams, pParams, false);

            var propVariant = new PropVariantBlob
            {
                vt = NativeAudio.VT_BLOB,
                cbSize = (uint)paramsSize,
                pBlobData = pParams,
            };
            Marshal.StructureToPtr(propVariant, pPropVariant, false);

            var handler = new ActivationHandler();
            NativeAudio.ActivateAudioInterfaceAsync(
                NativeAudio.VirtualAudioDeviceProcessLoopback,
                NativeAudio.IID_IAudioClient,
                pPropVariant,
                handler,
                out _);

            // ActivateAudioInterfaceAsync may complete on an MTA worker thread.
            if (!handler.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("ActivateAudioInterfaceAsync did not complete within 10s.");

            Check(handler.ActivateHResult, "process-loopback activation");
            return handler.AudioClient ?? throw new InvalidOperationException("Activation returned a null IAudioClient.");
        }
        finally
        {
            Marshal.FreeHGlobal(pParams);
            Marshal.FreeHGlobal(pPropVariant);
        }
    }

    private static void AccumulateLevels(byte[] buffer, int byteCount, ref long peak, ref double sumSquares, ref long sampleCount)
    {
        for (int i = 0; i + 1 < byteCount; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            int abs = Math.Abs((int)sample);
            if (abs > peak)
                peak = abs;
            sumSquares += (double)sample * sample;
            sampleCount++;
        }
    }

    private static double ToDb(double normalized)
        => normalized <= 0 ? double.NegativeInfinity : 20.0 * Math.Log10(normalized);

    private static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new COMException($"{what} failed (HRESULT 0x{hr:X8}).", hr);
    }

    /// <summary>
    /// COM callback target. The CLR builds a CCW so native code can invoke
    /// ActivateCompleted; we block the caller until it fires.
    /// </summary>
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly ManualResetEventSlim _done = new(false);

        public IAudioClient? AudioClient { get; private set; }
        public int ActivateHResult { get; private set; }

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                int hr = activateOperation.GetActivateResult(out int activateResult, out object iface);
                if (hr < 0)
                {
                    ActivateHResult = hr;
                }
                else
                {
                    ActivateHResult = activateResult;
                    if (activateResult >= 0)
                        AudioClient = (IAudioClient)iface;
                }
            }
            catch (Exception ex)
            {
                ActivateHResult = ex.HResult != 0 ? ex.HResult : unchecked((int)0x80004005);
            }
            finally
            {
                _done.Set();
            }

            return 0; // S_OK
        }

        public bool Wait(TimeSpan timeout) => _done.Wait(timeout);
    }
}

/// <summary>Measured result of a single capture run.</summary>
internal readonly record struct CaptureResult(
    double PeakDb,
    double RmsDb,
    double Peak,
    double Rms,
    double DurationSeconds,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    long DataBytes,
    bool Silent);
