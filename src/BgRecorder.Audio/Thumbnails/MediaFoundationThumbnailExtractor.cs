using System.Runtime.InteropServices;
using BgRecorder.Audio.Interop;
using BgRecorder.Core.Audio;

namespace BgRecorder.Audio.Thumbnails;

/// <summary>
/// <see cref="IThumbnailExtractor"/> built on Media Foundation. It opens the finalized MP4 with a source
/// reader that has video processing enabled, so an H.264 frame is decoded and colour-converted straight to
/// RGB32; it reads the first decoded frame, downscales it, and writes a 32-bit BMP. BMP keeps the encoder
/// dependency-free (this audio-side project references no image library and can't pull in WPF) — swapping
/// to a WIC-encoded JPEG for smaller files is a self-contained future change behind this same interface.
/// Strictly best-effort: any failure returns false and leaves finalize to continue without a thumbnail.
/// </summary>
public sealed class MediaFoundationThumbnailExtractor : IThumbnailExtractor
{
    /// <summary>Target thumbnail width in pixels; height follows the source aspect ratio. Never upscales.</summary>
    private const int ThumbnailWidth = 320;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, int coInit);

    public event Action<string>? Diagnostic;

    public Task<bool> TryExtractAsync(string videoMp4, string outputImagePath, CancellationToken ct = default)
    {
        if (!File.Exists(videoMp4))
        {
            return Task.FromResult(false);
        }

        return Task.Run(() =>
        {
            CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);
            MF.MFStartup(MF.MF_VERSION, MF.MFSTARTUP_FULL);
            try
            {
                Extract(videoMp4, outputImagePath, ct);
                return true;
            }
            catch (Exception ex)
            {
                Diagnostic?.Invoke($"Thumbnail extraction failed for {videoMp4}: {ex.Message}");
                TryDelete(outputImagePath);
                return false;
            }
            finally
            {
                MF.MFShutdown();
            }
        }, ct);
    }

    private static void Extract(string videoMp4, string outputImagePath, CancellationToken ct)
    {
        IMFAttributes? readerAttributes = null;
        IntPtr pReaderAttributes = IntPtr.Zero;
        IMFSourceReader? reader = null;
        IMFMediaType? rgbType = null;
        IMFMediaType? actualType = null;

        try
        {
            // Enable video processing so the reader can decode + colour-convert H.264 to RGB32.
            MF.MFCreateAttributes(out readerAttributes, 1);
            Guid enableProcessing = MF.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING;
            Check(readerAttributes.SetUINT32(ref enableProcessing, 1), "enable video processing");
            pReaderAttributes = Marshal.GetIUnknownForObject(readerAttributes);

            MF.MFCreateSourceReaderFromURL(videoMp4, pReaderAttributes, out reader);
            Check(reader.SetStreamSelection(MF.MF_SOURCE_READER_ALL_STREAMS, false), "SetStreamSelection(all,false)");
            Check(reader.SetStreamSelection(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, true), "SetStreamSelection(video,true)");

            MF.MFCreateMediaType(out rgbType);
            Guid major = MF.MF_MT_MAJOR_TYPE, majorVideo = MF.MFMediaType_Video;
            Guid subtype = MF.MF_MT_SUBTYPE, rgb32 = MF.MFVideoFormat_RGB32;
            Check(rgbType.SetGUID(ref major, ref majorVideo), "SetGUID(major=video)");
            Check(rgbType.SetGUID(ref subtype, ref rgb32), "SetGUID(subtype=rgb32)");
            Check(reader.SetCurrentMediaType(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, rgbType), "SetCurrentMediaType(rgb32)");

            Check(reader.GetCurrentMediaType(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out actualType), "GetCurrentMediaType");
            Guid frameSizeKey = MF.MF_MT_FRAME_SIZE;
            Check(actualType.GetUINT64(ref frameSizeKey, out ulong frameSize), "GetUINT64(frame size)");
            int width = (int)(frameSize >> 32);
            int height = (int)(frameSize & 0xFFFFFFFF);
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException($"Decoded frame reported a non-positive size ({width}x{height}).");
            }

            // Default stride tells us the row length and orientation (negative = bottom-up); absent it, RGB32
            // is a tight top-down width*4 rows.
            Guid strideKey = MF.MF_MT_DEFAULT_STRIDE;
            int stride = actualType.GetUINT32(ref strideKey, out uint strideRaw) >= 0 ? unchecked((int)strideRaw) : width * 4;
            bool topDown = stride >= 0;
            int absStride = Math.Abs(stride);

            byte[] frame = ReadFirstFrame(reader, absStride * height, ct);

            byte[] thumbnail = Downscale(frame, width, height, absStride, topDown, out int thumbWidth, out int thumbHeight);
            WriteBmp(outputImagePath, thumbnail, thumbWidth, thumbHeight);
        }
        finally
        {
            if (pReaderAttributes != IntPtr.Zero)
            {
                Marshal.Release(pReaderAttributes);
            }
            Release(actualType);
            Release(rgbType);
            Release(reader);
            Release(readerAttributes);
        }
    }

    private static byte[] ReadFirstFrame(IMFSourceReader reader, int expectedBytes, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Check(
                reader.ReadSample(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out _, out uint flags, out _, out IMFSample? sample),
                "ReadSample");

            if ((flags & MF.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
            {
                if (sample is not null)
                {
                    Release(sample);
                }
                throw new InvalidOperationException("The video ended before a decodable frame was produced.");
            }

            if (sample is null)
            {
                continue; // stream tick / gap — keep reading until a real frame arrives
            }

            try
            {
                Check(sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer), "ConvertToContiguousBuffer");
                try
                {
                    Check(buffer.Lock(out IntPtr ptr, out _, out uint currentLength), "buffer.Lock");
                    try
                    {
                        int count = Math.Min((int)currentLength, expectedBytes);
                        var frame = new byte[count];
                        Marshal.Copy(ptr, frame, 0, count);
                        return frame;
                    }
                    finally
                    {
                        Check(buffer.Unlock(), "buffer.Unlock");
                    }
                }
                finally
                {
                    Release(buffer);
                }
            }
            finally
            {
                Release(sample);
            }
        }
    }

    /// <summary>
    /// Nearest-neighbour downscale to <see cref="ThumbnailWidth"/> (aspect-preserving, never upscaling),
    /// normalising to a top-down BGRA buffer regardless of the source stride's sign.
    /// </summary>
    private static byte[] Downscale(
        byte[] frame, int width, int height, int absStride, bool topDown, out int thumbWidth, out int thumbHeight)
    {
        thumbWidth = Math.Min(ThumbnailWidth, width);
        thumbHeight = Math.Max(1, (int)Math.Round(thumbWidth * (double)height / width));

        var dst = new byte[thumbWidth * thumbHeight * 4];
        for (int y = 0; y < thumbHeight; y++)
        {
            int srcY = Math.Min(height - 1, y * height / thumbHeight);
            int bufRow = topDown ? srcY : height - 1 - srcY; // normalise bottom-up sources to visual top-down
            int rowBase = bufRow * absStride;
            for (int x = 0; x < thumbWidth; x++)
            {
                int srcX = Math.Min(width - 1, x * width / thumbWidth);
                int si = rowBase + srcX * 4;
                int di = (y * thumbWidth + x) * 4;
                if (si + 3 < frame.Length)
                {
                    dst[di] = frame[si];         // B
                    dst[di + 1] = frame[si + 1]; // G
                    dst[di + 2] = frame[si + 2]; // R
                }
                dst[di + 3] = 255;               // opaque
            }
        }

        return dst;
    }

    /// <summary>Writes a top-down 32-bit uncompressed BMP (BI_RGB) from a top-down BGRA buffer.</summary>
    private static void WriteBmp(string outputImagePath, byte[] bgra, int width, int height)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputImagePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        const int fileHeaderSize = 14;
        const int infoHeaderSize = 40;
        int imageSize = width * height * 4;

        using var stream = new FileStream(outputImagePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);

        // BITMAPFILEHEADER
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(fileHeaderSize + infoHeaderSize + imageSize); // bfSize
        writer.Write(0); // bfReserved1/2
        writer.Write(fileHeaderSize + infoHeaderSize); // bfOffBits

        // BITMAPINFOHEADER
        writer.Write(infoHeaderSize);
        writer.Write(width);
        writer.Write(-height); // negative height = top-down rows (matches our buffer)
        writer.Write((ushort)1); // planes
        writer.Write((ushort)32); // bits per pixel
        writer.Write(0); // BI_RGB
        writer.Write(imageSize);
        writer.Write(2835); // ~72 DPI (pixels/metre)
        writer.Write(2835);
        writer.Write(0); // colours used
        writer.Write(0); // colours important

        writer.Write(bgra);
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new COMException($"Media Foundation {what} failed (HRESULT 0x{hr:X8}).", hr);
        }
    }
}
