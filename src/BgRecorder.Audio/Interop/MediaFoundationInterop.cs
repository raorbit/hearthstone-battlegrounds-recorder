using System.Runtime.InteropServices;

namespace BgRecorder.Audio.Interop;

// ---------------------------------------------------------------------------
// Hand-rolled Media Foundation COM interop for the finalize muxer.
//
// Rationale for hand-rolling (vs. a wrapper like Vortice.MediaFoundation):
//   * the required surface is small and bounded — a Source Reader for the video,
//     a Source Reader for the audio, a two-stream Sink Writer, and a handful of
//     media-type attributes — so the interop cost is low;
//   * it removes a third-party dependency and the risk of guessing a wrapper's
//     evolving API shape; and
//   * it matches the pattern already established by the Spike D process-loopback
//     interop in this project.
//
// Only the vtable methods we call carry real signatures. All preceding methods in
// each interface are declared (as opaque slots) so the vtable order stays correct.
// These are stable OS APIs; the C# port is original.
// ---------------------------------------------------------------------------

internal static class MF
{
    public const uint MF_VERSION = 0x00020070; // MF_SDK_VERSION (0x0002) << 16 | MF_API_VERSION (0x0070)
    public const uint MFSTARTUP_FULL = 0;

    public const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
    public const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    public const uint MF_SOURCE_READER_FIRST_AUDIO_STREAM = 0xFFFFFFFD;

    public const uint MF_SOURCE_READERF_ERROR = 0x00000001;
    public const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002;
    public const uint MF_SOURCE_READERF_STREAMTICK = 0x00000100;

    // Media type attribute GUIDs.
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid MF_MT_AAC_PAYLOAD_TYPE = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
    public static readonly Guid MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION = new("7632f0e6-9538-4d61-acda-ea29c8c14456");

    // Major types and subtypes.
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00aa00389b71");

    // Uncompressed 32-bit RGB (X8R8G8B8 in memory as B,G,R,X little-endian) — the thumbnail decode target.
    public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");

    // Video media-type attributes for a decoded frame.
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");     // UINT64: (width << 32) | height
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"); // UINT32 (signed): row stride, negative when bottom-up

    // Lets the source reader insert the video processor MFT so it can decode + color-convert (and scale)
    // H.264 straight to RGB32 — required for a SetCurrentMediaType(RGB32) on a compressed source.
    public static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");

    public static readonly Guid IID_IMFSourceReader = new("70ae66f2-c809-4e4f-8915-bdcb406b7993");
    public static readonly Guid IID_IMFSinkWriter = new("3137f1cd-fe5e-4805-a5d8-fb477448cb3d");

    // When true the sink writer won't block WriteSample to keep streams interleaved.
    // Required for offline muxing where we feed one whole stream before the other.
    public static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");

    [DllImport("Mfplat.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void MFCreateAttributes(out IMFAttributes ppMFAttributes, uint cInitialSize);

    [DllImport("Mfplat.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void MFStartup(uint version, uint flags);

    [DllImport("Mfplat.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void MFShutdown();

    [DllImport("Mfplat.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void MFCreateMediaType(out IMFMediaType ppMFType);

    [DllImport("Mfplat.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void MFCreateMemoryBuffer(uint cbMaxLength, out IMFMediaBuffer ppBuffer);

    [DllImport("Mfplat.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void MFCreateSample(out IMFSample ppIMFSample);

    [DllImport("Mfreadwrite.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void MFCreateSourceReaderFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string pwszURL,
        IntPtr pAttributes,
        out IMFSourceReader ppSourceReader);

    [DllImport("Mfreadwrite.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void MFCreateSinkWriterFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string pwszOutputURL,
        IntPtr pByteStream,
        IntPtr pAttributes,
        out IMFSinkWriter ppSinkWriter);
}

[ComImport]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    // 1-4: GetItem / GetItemType / CompareItem / Compare
    [PreserveSig] int GetItem(IntPtr a, IntPtr b);
    [PreserveSig] int GetItemType(IntPtr a, IntPtr b);
    [PreserveSig] int CompareItem(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int Compare(IntPtr a, IntPtr b, IntPtr c);
    // 5-15: typed getters
    [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
    [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);
    [PreserveSig] int GetDouble(IntPtr a, IntPtr b);
    [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength(IntPtr a, IntPtr b);
    [PreserveSig] int GetString(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
    [PreserveSig] int GetAllocatedString(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int GetBlobSize(IntPtr a, IntPtr b);
    [PreserveSig] int GetBlob(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
    [PreserveSig] int GetAllocatedBlob(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int GetUnknown(IntPtr a, IntPtr b, IntPtr c);
    // 16-18: SetItem / DeleteItem / DeleteAllItems
    [PreserveSig] int SetItem(IntPtr a, IntPtr b);
    [PreserveSig] int DeleteItem(IntPtr a);
    [PreserveSig] int DeleteAllItems();
    // 19-25: typed setters
    [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
    [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
    [PreserveSig] int SetDouble(IntPtr a, double b);
    [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
    [PreserveSig] int SetString(IntPtr a, IntPtr b);
    [PreserveSig] int SetBlob([In] ref Guid key, byte[] buf, uint cbBufSize);
    [PreserveSig] int SetUnknown(IntPtr a, IntPtr b);
    // 26-30: lock/count/index/copy
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint pcItems);
    [PreserveSig] int GetItemByIndex(uint index, IntPtr a, IntPtr b);
    [PreserveSig] int CopyAllItems(IMFAttributes dest);
}

[ComImport]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
    // Inherited 30 IMFAttributes methods precede these.
    [PreserveSig] int GetMajorType(out Guid guid);
    [PreserveSig] int IsCompressedFormat(out int compressed);
    [PreserveSig] int IsEqual(IMFMediaType type, out uint flags);
    [PreserveSig] int GetRepresentation(Guid rep, out IntPtr data);
    [PreserveSig] int FreeRepresentation(Guid rep, IntPtr data);
}

[ComImport]
[Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
    [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
    [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
}

// Declared self-contained (not via ": IMFAttributes") on purpose: .NET COM interop
// does NOT flatten an inherited ComImport interface's methods into the derived vtable,
// so IMFSample's own methods must sit after all 30 IMFAttributes slots, declared inline.
// We never call the attribute methods on a sample, so they are opaque placeholders.
[ComImport]
[Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    // --- 30 IMFAttributes slots (unused here) ---
    [PreserveSig] int Attr01(); [PreserveSig] int Attr02(); [PreserveSig] int Attr03();
    [PreserveSig] int Attr04(); [PreserveSig] int Attr05(); [PreserveSig] int Attr06();
    [PreserveSig] int Attr07(); [PreserveSig] int Attr08(); [PreserveSig] int Attr09();
    [PreserveSig] int Attr10(); [PreserveSig] int Attr11(); [PreserveSig] int Attr12();
    [PreserveSig] int Attr13(); [PreserveSig] int Attr14(); [PreserveSig] int Attr15();
    [PreserveSig] int Attr16(); [PreserveSig] int Attr17(); [PreserveSig] int Attr18();
    [PreserveSig] int Attr19(); [PreserveSig] int Attr20(); [PreserveSig] int Attr21();
    [PreserveSig] int Attr22(); [PreserveSig] int Attr23(); [PreserveSig] int Attr24();
    [PreserveSig] int Attr25(); [PreserveSig] int Attr26(); [PreserveSig] int Attr27();
    [PreserveSig] int Attr28(); [PreserveSig] int Attr29(); [PreserveSig] int Attr30();
    // --- IMFSample methods ---
    [PreserveSig] int GetSampleFlags(out uint flags);
    [PreserveSig] int SetSampleFlags(uint flags);
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    [PreserveSig] int GetSampleDuration(out long duration);
    [PreserveSig] int SetSampleDuration(long duration);
    [PreserveSig] int GetBufferCount(out uint count);
    [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
}

[ComImport]
[Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSourceReader
{
    [PreserveSig] int GetStreamSelection(uint streamIndex, out int selected);
    [PreserveSig] int SetStreamSelection(uint streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
    [PreserveSig] int GetNativeMediaType(uint streamIndex, uint mediaTypeIndex, out IMFMediaType mediaType);
    [PreserveSig] int GetCurrentMediaType(uint streamIndex, out IMFMediaType mediaType);
    [PreserveSig] int SetCurrentMediaType(uint streamIndex, IntPtr reserved, IMFMediaType mediaType);
    [PreserveSig] int SetCurrentPosition([In] ref Guid guidTimeFormat, IntPtr varPosition);
    [PreserveSig]
    int ReadSample(
        uint streamIndex,
        uint controlFlags,
        out uint actualStreamIndex,
        out uint streamFlags,
        out long timestamp,
        out IMFSample? sample);
    [PreserveSig] int Flush(uint streamIndex);
    [PreserveSig] int GetServiceForStream(uint a, [In] ref Guid b, [In] ref Guid c, out IntPtr d);
    [PreserveSig] int GetPresentationAttribute(uint a, [In] ref Guid b, IntPtr c);
}

[ComImport]
[Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSinkWriter
{
    [PreserveSig] int AddStream(IMFMediaType targetMediaType, out uint streamIndex);
    [PreserveSig] int SetInputMediaType(uint streamIndex, IMFMediaType inputMediaType, IntPtr encodingParameters);
    [PreserveSig] int BeginWriting();
    [PreserveSig] int WriteSample(uint streamIndex, IMFSample sample);
    [PreserveSig] int SendStreamTick(uint streamIndex, long timestamp);
    [PreserveSig] int PlaceMarker(uint streamIndex, IntPtr context);
    [PreserveSig] int NotifyEndOfSegment(uint streamIndex);
    [PreserveSig] int Flush(uint streamIndex);
    [PreserveSig] int DoFinalize();
    [PreserveSig] int GetServiceForStream(uint a, [In] ref Guid b, [In] ref Guid c, out IntPtr d);
    [PreserveSig] int GetStatistics(uint a, IntPtr b);
}
