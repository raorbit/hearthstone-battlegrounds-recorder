using BgRecorder.Audio.Thumbnails;
using Xunit;

namespace BgRecorder.Audio.Tests;

/// <summary>
/// Integration test for the Media Foundation thumbnail extractor. It decodes the committed H.264 fixture
/// and asserts a valid, sanely-sized BMP is written. Uses a VISIBLE SKIP when the fixture is absent so
/// the only coverage of the hand-rolled MF decode path can never go falsely green. Unlike the muxer
/// tests it needs no ffprobe — the BMP header is parsed directly.
/// </summary>
public class ThumbnailExtractorTests : IDisposable
{
    private readonly string _dir;

    public ThumbnailExtractorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bg-thumb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [ThumbnailAssetsFact]
    public async Task Extract_WritesAValidDownscaledBmp()
    {
        string video = MuxerAssets.ResolveVideo()!;
        string output = Path.Combine(_dir, "thumb.bmp");

        bool ok = await new MediaFoundationThumbnailExtractor().TryExtractAsync(video, output, CancellationToken.None);

        Assert.True(ok, "extraction reported failure");
        Assert.True(File.Exists(output));

        var bytes = await File.ReadAllBytesAsync(output);
        Assert.True(bytes.Length > 54, "BMP is smaller than its own header");
        Assert.Equal((byte)'B', bytes[0]);
        Assert.Equal((byte)'M', bytes[1]);

        int width = BitConverter.ToInt32(bytes, 18);
        int height = BitConverter.ToInt32(bytes, 22);
        short bitCount = BitConverter.ToInt16(bytes, 28);

        Assert.Equal(32, bitCount);
        Assert.InRange(width, 1, 320);          // downscaled, never upscaled past the target width
        Assert.True(Math.Abs(height) >= 1);     // top-down BMP stores height as negative
        Assert.True(bytes.Length >= 54 + width * Math.Abs(height) * 4, "pixel data is shorter than the declared frame");
    }

    [Fact]
    public async Task Extract_ReturnsFalseForAMissingFile()
    {
        bool ok = await new MediaFoundationThumbnailExtractor()
            .TryExtractAsync(Path.Combine(_dir, "does-not-exist.mp4"), Path.Combine(_dir, "out.bmp"), CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task Extract_ReturnsFalseForANonVideoFile()
    {
        // A best-effort extractor must swallow a decode failure (here: a text file with an .mp4 name)
        // and report false rather than throwing into the finalize path.
        string notVideo = Path.Combine(_dir, "not-really.mp4");
        await File.WriteAllTextAsync(notVideo, "this is not an mp4");

        bool ok = await new MediaFoundationThumbnailExtractor()
            .TryExtractAsync(notVideo, Path.Combine(_dir, "out.bmp"), CancellationToken.None);

        Assert.False(ok);
    }
}

/// <summary>Skips (visibly) unless the H.264 video fixture is present — no ffprobe needed for this one.</summary>
public sealed class ThumbnailAssetsFactAttribute : FactAttribute
{
    public ThumbnailAssetsFactAttribute()
    {
        if (MuxerAssets.ResolveVideo() is null)
        {
            Skip = "Thumbnail video fixture not found. Set BG_SCRATCHPAD to a folder with spikeB-capture.mp4, "
                 + "or add tests/BgRecorder.Audio.Tests/fixtures/sample-h264.mp4 (a real H.264 MP4 to decode).";
        }
    }
}
