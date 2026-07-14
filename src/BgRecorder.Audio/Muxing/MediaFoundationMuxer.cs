using System.Runtime.InteropServices;
using BgRecorder.Audio.Interop;
using BgRecorder.Audio.Mixing;
using BgRecorder.Core.Audio;

namespace BgRecorder.Audio.Muxing;

/// <summary>
/// <see cref="IMuxer"/> built on Media Foundation. The staged H.264 video is remuxed
/// with no re-encode (the source reader delivers native compressed samples straight to
/// the sink writer's passthrough input), while the WAV is normalized to 44.1 kHz /
/// 16-bit / stereo PCM and encoded to AAC by the sink writer. The audio offset is applied
/// as leading silence plus a matching sample-time shift, so a positive offset makes the
/// audio start later without a hard timing gap. An empty <c>audioWav</c> means no audio
/// (a dead audio capture must never block the video): the video is remuxed alone.
/// Never shells out to ffmpeg.
/// </summary>
public sealed class MediaFoundationMuxer : IMuxer
{
    private const int AudioSampleRate = 44100;
    private const int AudioChannels = 2;
    private const int AudioBits = 16;
    private const int AudioBlockAlign = AudioChannels * (AudioBits / 8);
    private const int AudioBytesPerSecond = AudioSampleRate * AudioBlockAlign;
    private const uint EndOfStream = MF.MF_SOURCE_READERF_ENDOFSTREAM;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, int coInit);

    public Task MuxAsync(string videoMp4, string audioWav, TimeSpan audioOffset, string outputMp4, CancellationToken ct)
    {
        if (!File.Exists(videoMp4)) throw new FileNotFoundException("Video not found.", videoMp4);
        if (!string.IsNullOrEmpty(audioWav) && !File.Exists(audioWav)) throw new FileNotFoundException("Audio not found.", audioWav);

        return Task.Run(() =>
        {
            CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);
            MF.MFStartup(MF.MF_VERSION, MF.MFSTARTUP_FULL);
            try
            {
                Mux(videoMp4, audioWav, audioOffset, outputMp4, ct);
            }
            catch
            {
                TryDelete(outputMp4);
                throw;
            }
            finally
            {
                MF.MFShutdown();
            }
        }, ct);
    }

    private static void Mux(string videoMp4, string audioWav, TimeSpan audioOffset, string outputMp4, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputMp4))!);
        TryDelete(outputMp4);

        bool hasAudio = !string.IsNullOrEmpty(audioWav);

        IMFSourceReader? videoReader = null;
        IMFSourceReader? audioReader = null;
        IMFSinkWriter? sink = null;
        IMFMediaType? videoType = null;
        IMFMediaType? pcmType = null;
        IMFMediaType? aacType = null;
        IMFAttributes? sinkAttributes = null;
        IntPtr pSinkAttributes = IntPtr.Zero;

        try
        {
            // --- Video source: select the video stream, keep native (compressed) samples ---
            MF.MFCreateSourceReaderFromURL(videoMp4, IntPtr.Zero, out videoReader);
            Check(videoReader.SetStreamSelection(MF.MF_SOURCE_READER_ALL_STREAMS, false), "video SetStreamSelection(all,false)");
            Check(videoReader.SetStreamSelection(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, true), "video SetStreamSelection(video,true)");
            Check(videoReader.GetNativeMediaType(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out videoType), "video GetNativeMediaType");

            // --- Audio source (when present): decode + resample WAV to 44.1k/16/2 PCM ---
            if (hasAudio)
            {
                pcmType = CreatePcmType();
                MF.MFCreateSourceReaderFromURL(audioWav, IntPtr.Zero, out audioReader);
                Check(audioReader.SetStreamSelection(MF.MF_SOURCE_READER_ALL_STREAMS, false), "audio SetStreamSelection(all,false)");
                Check(audioReader.SetStreamSelection(MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM, true), "audio SetStreamSelection(audio,true)");
                Check(audioReader.SetCurrentMediaType(MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM, IntPtr.Zero, pcmType), "audio SetCurrentMediaType(pcm)");
            }

            // --- Sink writer: video passthrough stream + (optionally) an AAC-encoded audio stream ---
            // Disable throttling: this muxer feeds one whole stream before the other,
            // so the sink writer must not block WriteSample waiting for interleaving.
            MF.MFCreateAttributes(out sinkAttributes, 1);
            Guid throttleKey = MF.MF_SINK_WRITER_DISABLE_THROTTLING;
            Check(sinkAttributes.SetUINT32(ref throttleKey, 1), "SetUINT32(disable throttling)");
            pSinkAttributes = Marshal.GetIUnknownForObject(sinkAttributes);
            MF.MFCreateSinkWriterFromURL(outputMp4, IntPtr.Zero, pSinkAttributes, out sink);
            Check(sink.AddStream(videoType, out uint videoStream), "AddStream(video)");
            Check(sink.SetInputMediaType(videoStream, videoType, IntPtr.Zero), "SetInputMediaType(video)");

            uint audioStream = 0;
            if (hasAudio)
            {
                aacType = CreateAacType();
                Check(sink.AddStream(aacType, out audioStream), "AddStream(aac)");
                Check(sink.SetInputMediaType(audioStream, pcmType!, IntPtr.Zero), "SetInputMediaType(pcm)");
            }

            Check(sink.BeginWriting(), "BeginWriting");

            PumpVideo(videoReader, sink, videoStream, ct);
            if (hasAudio)
            {
                PumpAudio(audioReader!, sink, audioStream, audioOffset, ct);
            }

            Check(sink.DoFinalize(), "Finalize");
        }
        finally
        {
            if (pSinkAttributes != IntPtr.Zero)
                Marshal.Release(pSinkAttributes);
            Release(sink);
            Release(videoReader);
            Release(audioReader);
            Release(videoType);
            Release(pcmType);
            Release(aacType);
            Release(sinkAttributes);
        }
    }

    private static void PumpVideo(IMFSourceReader reader, IMFSinkWriter sink, uint sinkStream, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Check(reader.ReadSample(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out _, out uint flags, out _, out IMFSample? sample),
                "video ReadSample");

            if ((flags & EndOfStream) != 0)
            {
                if (sample is not null) Release(sample);
                break;
            }
            if (sample is null)
                continue; // stream tick / gap

            Check(sink.WriteSample(sinkStream, sample), "WriteSample(video)");
            Release(sample);
        }
    }

    private static void PumpAudio(IMFSourceReader reader, IMFSinkWriter sink, uint sinkStream, TimeSpan offset, CancellationToken ct)
    {
        long shiftHns = offset.Ticks; // TimeSpan ticks are already 100-ns units.

        if (offset > TimeSpan.Zero)
            WriteLeadingSilence(sink, sinkStream, offset, ct);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Check(reader.ReadSample(MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, out _, out uint flags, out _, out IMFSample? sample),
                "audio ReadSample");

            if ((flags & EndOfStream) != 0)
            {
                if (sample is not null) Release(sample);
                break;
            }
            if (sample is null)
                continue;

            Check(sample.GetSampleTime(out long t), "GetSampleTime");
            long newTime = t + shiftHns;
            if (newTime < 0)
            {
                // Negative offset: the audio started before the video's first frame. Only the
                // sub-zero portion should be dropped — NOT the whole buffer. Since the caller's
                // offset (audioClock - videoClock) is commonly a small fraction of a reader
                // buffer, dropping the whole buffer would open a leading A/V gap and discard
                // real audio. Trim just the frames before output time 0 and rebase the
                // remainder to 0; a buffer that lies entirely before 0 is genuinely dropped.
                if (TryTrimLeadingToZero(sample, newTime, out IMFSample? trimmed))
                {
                    Check(sink.WriteSample(sinkStream, trimmed!), "WriteSample(audio-trimmed)");
                    Release(trimmed);
                }
                Release(sample);
                continue;
            }

            Check(sample.SetSampleTime(newTime), "SetSampleTime");
            Check(sink.WriteSample(sinkStream, sample), "WriteSample(audio)");
            Release(sample);
        }
    }

    /// <summary>
    /// For a straddling audio buffer whose shifted start time is negative, builds a new PCM sample
    /// containing only the frames at/after output time 0 (rebased to time 0). Returns false — with
    /// no output sample — when the entire buffer lies before 0 and nothing should be kept. Because
    /// the trimmed remainder starts exactly at 0 and the next source buffer's shifted time equals
    /// this buffer's original end, playback stays gapless and contiguous.
    /// </summary>
    private static bool TryTrimLeadingToZero(IMFSample source, long newTimeHns, out IMFSample? trimmed)
    {
        trimmed = null;

        long framesToTrim = (-newTimeHns) * AudioSampleRate / 10_000_000L;
        long bytesToTrim = framesToTrim * AudioBlockAlign;

        Check(source.ConvertToContiguousBuffer(out IMFMediaBuffer buffer), "ConvertToContiguousBuffer");
        try
        {
            Check(buffer.Lock(out IntPtr ptr, out _, out uint currentLength), "trim buffer.Lock");
            try
            {
                if (bytesToTrim >= currentLength)
                    return false; // whole buffer is before output time 0 — genuinely dropped

                int keepBytes = (int)(currentLength - bytesToTrim);
                var kept = new byte[keepBytes];
                Marshal.Copy(IntPtr.Add(ptr, (int)bytesToTrim), kept, 0, keepBytes);

                long keepFrames = keepBytes / AudioBlockAlign;
                long durationHns = keepFrames * 10_000_000L / AudioSampleRate;
                trimmed = CreatePcmSample(kept, keepBytes, 0, durationHns);
                return true;
            }
            finally
            {
                Check(buffer.Unlock(), "trim buffer.Unlock");
            }
        }
        finally
        {
            Release(buffer);
        }
    }

    private static void WriteLeadingSilence(IMFSinkWriter sink, uint sinkStream, TimeSpan offset, CancellationToken ct)
    {
        long totalFrames = PcmMath.SilenceFrameCount(AudioSampleRate, offset);
        long framesWritten = 0;
        const int chunkFrames = AudioSampleRate; // ~1 s per silence sample

        var zeros = new byte[chunkFrames * AudioBlockAlign];

        while (framesWritten < totalFrames)
        {
            ct.ThrowIfCancellationRequested();
            int frames = (int)Math.Min(chunkFrames, totalFrames - framesWritten);
            int bytes = frames * AudioBlockAlign;

            long timeHns = framesWritten * 10_000_000L / AudioSampleRate;
            long durHns = frames * 10_000_000L / AudioSampleRate;

            IMFSample sample = CreatePcmSample(zeros, bytes, timeHns, durHns);
            Check(sink.WriteSample(sinkStream, sample), "WriteSample(silence)");
            Release(sample);

            framesWritten += frames;
        }
    }

    private static IMFSample CreatePcmSample(byte[] data, int byteCount, long timeHns, long durationHns)
    {
        MF.MFCreateMemoryBuffer((uint)byteCount, out IMFMediaBuffer buffer);
        Check(buffer.Lock(out IntPtr ptr, out _, out _), "buffer.Lock");
        Marshal.Copy(data, 0, ptr, byteCount);
        Check(buffer.Unlock(), "buffer.Unlock");
        Check(buffer.SetCurrentLength((uint)byteCount), "buffer.SetCurrentLength");

        MF.MFCreateSample(out IMFSample sample);
        Check(sample.AddBuffer(buffer), "sample.AddBuffer");
        Check(sample.SetSampleTime(timeHns), "sample.SetSampleTime");
        Check(sample.SetSampleDuration(durationHns), "sample.SetSampleDuration");

        Release(buffer);
        return sample;
    }

    private static IMFMediaType CreatePcmType()
    {
        MF.MFCreateMediaType(out IMFMediaType t);
        SetGuid(t, MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Audio);
        SetGuid(t, MF.MF_MT_SUBTYPE, MF.MFAudioFormat_PCM);
        SetU32(t, MF.MF_MT_AUDIO_NUM_CHANNELS, AudioChannels);
        SetU32(t, MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, AudioSampleRate);
        SetU32(t, MF.MF_MT_AUDIO_BITS_PER_SAMPLE, AudioBits);
        SetU32(t, MF.MF_MT_AUDIO_BLOCK_ALIGNMENT, AudioBlockAlign);
        SetU32(t, MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, AudioBytesPerSecond);
        SetU32(t, MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
        return t;
    }

    private static IMFMediaType CreateAacType()
    {
        MF.MFCreateMediaType(out IMFMediaType t);
        SetGuid(t, MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Audio);
        SetGuid(t, MF.MF_MT_SUBTYPE, MF.MFAudioFormat_AAC);
        SetU32(t, MF.MF_MT_AUDIO_NUM_CHANNELS, AudioChannels);
        SetU32(t, MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, AudioSampleRate);
        SetU32(t, MF.MF_MT_AUDIO_BITS_PER_SAMPLE, AudioBits);
        SetU32(t, MF.MF_MT_AUDIO_BLOCK_ALIGNMENT, 1);
        SetU32(t, MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 16000); // 128 kbps AAC
        SetU32(t, MF.MF_MT_AAC_PAYLOAD_TYPE, 0);
        SetU32(t, MF.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29);
        return t;
    }

    private static void SetGuid(IMFMediaType t, Guid key, Guid value)
        => Check(t.SetGUID(ref key, ref value), "SetGUID");

    private static void SetU32(IMFMediaType t, Guid key, uint value)
        => Check(t.SetUINT32(ref key, value), "SetUINT32");

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
            Marshal.ReleaseComObject(comObject);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new COMException($"Media Foundation {what} failed (HRESULT 0x{hr:X8}).", hr);
    }
}
