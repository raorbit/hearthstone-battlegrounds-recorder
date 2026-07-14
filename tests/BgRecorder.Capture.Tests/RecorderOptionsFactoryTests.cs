using BgRecorder.Capture.Internal;
using BgRecorder.Core.Capture;
using ScreenRecorderLib;
using Xunit;

namespace BgRecorder.Capture.Tests;

public sealed class RecorderOptionsFactoryTests
{
    [Fact]
    public void CreateSource_binds_handle_and_disables_cursor_border_with_minimal_preview()
    {
        nint handle = 0xABCD;
        var source = RecorderOptionsFactory.CreateSource(handle);

        Assert.Equal(handle, source.Handle);
        Assert.False(source.IsCursorCaptureEnabled);
        Assert.False(source.IsBorderRequired);
        // Preview is enabled only to obtain the first-frame timestamp, at a deliberately tiny size.
        Assert.True(source.IsVideoFramePreviewEnabled);
        Assert.True(source.VideoFramePreviewSize.Width <= 32);
        Assert.True(source.VideoFramePreviewSize.Height <= 32);
    }

    [Theory]
    [InlineData(60, 12, true)]
    [InlineData(30, 8, false)]
    public void Build_maps_video_options_to_hardware_h264_cbr(int fps, int mbps, bool fragmented)
    {
        var options = new VideoOptions { Fps = fps, BitrateMbps = mbps, FragmentedMp4 = fragmented };
        var source = RecorderOptionsFactory.CreateSource(0x1);

        var opts = RecorderOptionsFactory.Build(source, options);

        Assert.Equal(RecorderMode.Video, opts.OutputOptions.RecorderMode);
        Assert.Contains(source, opts.SourceOptions.RecordingSources);

        var venc = opts.VideoEncoderOptions;
        Assert.Equal(mbps * 1_000_000, venc.Bitrate);
        Assert.Equal(fps, venc.Framerate);
        Assert.Equal(fragmented, venc.IsFragmentedMp4Enabled);
        Assert.True(venc.IsHardwareEncodingEnabled);
        Assert.True(venc.IsFixedFramerate);
        Assert.False(venc.IsMp4FastStartEnabled); // crash-safety: no end-of-file moov rewrite

        var h264 = Assert.IsType<H264VideoEncoder>(venc.Encoder);
        Assert.Equal(H264BitrateControlMode.CBR, h264.BitrateMode);
        Assert.Equal(H264Profile.High, h264.EncoderProfile);
    }

    [Fact]
    public void Build_disables_audio_track()
    {
        var opts = RecorderOptionsFactory.Build(RecorderOptionsFactory.CreateSource(0x1), new VideoOptions());
        Assert.Equal(false, opts.AudioOptions.IsAudioEnabled);
    }

    [Fact]
    public void EnsureStagingDirectory_creates_missing_parent()
    {
        var root = Path.Combine(Path.GetTempPath(), "bgrec-capture-tests", Guid.NewGuid().ToString("N"));
        var stagingFile = Path.Combine(root, "nested", "match.mp4");
        try
        {
            Assert.False(Directory.Exists(Path.GetDirectoryName(stagingFile)));

            RecorderOptionsFactory.EnsureStagingDirectory(stagingFile);

            Assert.True(Directory.Exists(Path.GetDirectoryName(stagingFile)));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnsureStagingDirectory_is_idempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "bgrec-capture-tests", Guid.NewGuid().ToString("N"));
        var stagingFile = Path.Combine(root, "match.mp4");
        try
        {
            RecorderOptionsFactory.EnsureStagingDirectory(stagingFile);
            RecorderOptionsFactory.EnsureStagingDirectory(stagingFile); // must not throw
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
