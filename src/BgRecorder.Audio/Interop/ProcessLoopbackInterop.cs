using System.Runtime.InteropServices;

namespace BgRecorder.Audio.Interop;

// ---------------------------------------------------------------------------
// Hand-ported Win32/COM interop for WASAPI *process* loopback capture,
// salvaged from spikes/SpikeD.ProcessAudio/Interop.cs (same repo, same author).
//
// Pattern reference (behaviour only, no code reuse): Microsoft's public
// "ApplicationLoopback" C++ sample and the audioclientactivationparams /
// mmdeviceapi headers. These are OS APIs; the C# port here is original.
//
// The one API NAudio 2.2.1 does NOT wrap is ActivateAudioInterfaceAsync with
// AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK, so we declare the minimal COM
// surface ourselves and reuse NAudio only for WAV plumbing.
// ---------------------------------------------------------------------------

internal enum AudioClientActivationType
{
    Default = 0,
    ProcessLoopback = 1,
}

internal enum ProcessLoopbackMode
{
    IncludeTargetProcessTree = 0,
    ExcludeTargetProcessTree = 1,
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientProcessLoopbackParams
{
    public uint TargetProcessId;
    public ProcessLoopbackMode ProcessLoopbackMode;
}

// AUDIOCLIENT_ACTIVATION_PARAMS: ActivationType (4) + union{ ProcessLoopbackParams (8) } = 12 bytes.
[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public AudioClientActivationType ActivationType;
    public AudioClientProcessLoopbackParams ProcessLoopbackParams;
}

// PROPVARIANT specialized to the VT_BLOB case we need. On x64 the pointer field
// is naturally 8-byte aligned (offset 16); total size 24 bytes — matches PROPVARIANT.
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariantBlob
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public uint cbSize;
    public IntPtr pBlobData;
}

// WAVEFORMATEX — 18 bytes, packed.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatEx
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig]
    int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig]
    int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

// IAudioClient — all 12 methods declared to preserve vtable order; only the ones
// we use carry real signatures, the rest are stubbed with IntPtr.
[ComImport]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
    [PreserveSig]
    int GetBufferSize(out uint numBufferFrames);
    [PreserveSig]
    int GetStreamLatency(out long latency);
    [PreserveSig]
    int GetCurrentPadding(out uint numPaddingFrames);
    [PreserveSig]
    int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);
    [PreserveSig]
    int GetMixFormat(out IntPtr deviceFormat);
    [PreserveSig]
    int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
    [PreserveSig]
    int Start();
    [PreserveSig]
    int Stop();
    [PreserveSig]
    int Reset();
    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);
    [PreserveSig]
    int GetService([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}

[ComImport]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(out IntPtr dataBuffer, out uint numFramesToRead, out uint bufferFlags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig]
    int ReleaseBuffer(uint numFramesRead);
    [PreserveSig]
    int GetNextPacketSize(out uint numFramesInNextPacket);
}

internal static class NativeAudio
{
    // The magic device path that routes ActivateAudioInterfaceAsync into the
    // process-loopback virtual device: VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK.
    public const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";

    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    public const ushort WAVE_FORMAT_PCM = 1;
    public const ushort VT_BLOB = 65;

    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);
}
