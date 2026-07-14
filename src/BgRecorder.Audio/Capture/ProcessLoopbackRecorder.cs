using System.Runtime.InteropServices;
using BgRecorder.Audio.Interop;
using NAudio.Wave;

namespace BgRecorder.Audio.Capture;

/// <summary>
/// Streaming game-only capture via AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
/// (Windows build 20348+). Activation and the capture loop run on a dedicated MTA
/// thread; <see cref="Start"/> blocks only until activation succeeds or fails, so an
/// activation failure surfaces synchronously and the engine can fall back to system
/// loopback. Output is fixed 44.1 kHz / 16-bit / stereo PCM.
/// </summary>
internal sealed class ProcessLoopbackRecorder : IWavRecorder
{
    public const int SampleRate = 44100;
    public const int Channels = 2;
    public const int BitsPerSample = 16;

    private readonly uint _targetPid;
    private readonly bool _includeProcessTree;
    private readonly string _outPath;

    private Thread? _thread;
    private volatile bool _stop;
    private readonly ManualResetEventSlim _ready = new(false);
    private Exception? _startException;
    private long _dataBytes;
    private long _firstSampleTicks; // UTC ticks of the first delivered sample; 0 = not yet seen.
    private int _failedRaised;
    private int _disposed;

    public ProcessLoopbackRecorder(uint targetPid, bool includeProcessTree, string outPath)
    {
        _targetPid = targetPid;
        _includeProcessTree = includeProcessTree;
        _outPath = outPath;
    }

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
        _thread = new Thread(CaptureThread)
        {
            IsBackground = true,
            Name = "bg-audio-process-loopback",
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();

        _ready.Wait();
        if (_startException is not null)
            throw _startException;
    }

    public TimeSpan Stop()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(5));
        long avgBytesPerSec = SampleRate * Channels * BitsPerSample / 8;
        return TimeSpan.FromSeconds(_dataBytes / (double)avgBytesPerSec);
    }

    public void Dispose()
    {
        // Idempotent: AudioCaptureSession disposes recorders on stop (to free the WAV
        // handle before mixing) and again from DisposeAsync.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
    }

    private void CaptureThread()
    {
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        WaveFileWriter? writer = null;
        try
        {
            audioClient = ActivateProcessLoopbackClient(
                _targetPid,
                _includeProcessTree ? ProcessLoopbackMode.IncludeTargetProcessTree : ProcessLoopbackMode.ExcludeTargetProcessTree);

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
                Check(audioClient.Initialize(NativeAudio.AUDCLNT_SHAREMODE_SHARED, streamFlags, bufferDuration, 0, pFormat, IntPtr.Zero),
                    "IAudioClient.Initialize");
            }
            finally
            {
                Marshal.FreeHGlobal(pFormat);
            }

            Guid captureIid = NativeAudio.IID_IAudioCaptureClient;
            Check(audioClient.GetService(ref captureIid, out object captureObj), "IAudioClient.GetService(IAudioCaptureClient)");
            captureClient = (IAudioCaptureClient)captureObj;

            using var sampleReady = new AutoResetEvent(false);
            Check(audioClient.SetEventHandle(sampleReady.SafeWaitHandle.DangerousGetHandle()), "IAudioClient.SetEventHandle");

            writer = new WaveFileWriter(_outPath, new WaveFormat(SampleRate, BitsPerSample, Channels));
            var scratch = new byte[format.nAvgBytesPerSec];

            Check(audioClient.Start(), "IAudioClient.Start");
            // Activation and initialization succeeded: unblock the caller.
            _ready.Set();

            try
            {
                while (!_stop)
                {
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

                            // Publish the first-sample wall clock exactly once, atomically.
                            if (Interlocked.Read(ref _firstSampleTicks) == 0)
                                Interlocked.CompareExchange(ref _firstSampleTicks, DateTimeOffset.UtcNow.UtcTicks, 0);

                            bool silent = (flags & NativeAudio.AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                            if (silent || dataPtr == IntPtr.Zero)
                                Array.Clear(scratch, 0, byteCount);
                            else
                                Marshal.Copy(dataPtr, scratch, 0, byteCount);

                            writer.Write(scratch, 0, byteCount);
                            _dataBytes += byteCount;
                        }

                        Check(captureClient.ReleaseBuffer(frames), "ReleaseBuffer");
                    }
                }
            }
            catch (Exception ex)
            {
                // Device loss / driver error mid-capture: keep what we have, surface it,
                // and let Stop() return the partial WAV. Never propagate into the void.
                RaiseFailed($"Process-loopback capture stopped: {ex.Message}");
            }
            finally
            {
                try { audioClient.Stop(); } catch { /* already gone */ }
            }
        }
        catch (Exception ex)
        {
            // Failure before we ever started streaming (typically activation): report it
            // synchronously through Start() so the engine can fall back to system loopback.
            _startException = ex;
        }
        finally
        {
            writer?.Dispose();
            if (captureClient is not null)
                Marshal.ReleaseComObject(captureClient);
            if (audioClient is not null)
                Marshal.ReleaseComObject(audioClient);
            _ready.Set();
        }
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

    private void RaiseFailed(string message)
    {
        if (Interlocked.Exchange(ref _failedRaised, 1) == 0)
            Failed?.Invoke(message);
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new COMException($"{what} failed (HRESULT 0x{hr:X8}).", hr);
    }

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
