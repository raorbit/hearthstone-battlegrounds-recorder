using BgRecorder.Core.Capture;
using ScreenRecorderLib;

namespace BgRecorder.Capture.Internal;

/// <summary>
/// Builds the ScreenRecorderLib recording graph from the app's <see cref="VideoOptions"/>:
/// a single window source, hardware H.264 CBR, audio disabled (audio is a separate engine),
/// fragmented MP4 for crash-safety. Kept as a pure factory so the option mapping is unit-testable.
/// </summary>
internal static class RecorderOptionsFactory
{
    /// <summary>
    /// Preview is enabled at a deliberately tiny size purely so <c>OnFrameRecorded</c> fires and the
    /// first encoded frame can be wall-clock stamped; the session disables it after that frame.
    /// The size is irrelevant (the pixels are never consumed) — small keeps the worst case cheap.
    /// </summary>
    private const int PreviewEdgePx = 16;

    /// <summary>Create the window source that both the options graph and the dynamic-disable path share.</summary>
    public static WindowRecordingSource CreateSource(nint windowHandle) => new(windowHandle)
    {
        IsCursorCaptureEnabled = false,
        // Yellow WGC capture border off: this is unattended background recording, not a screen-share.
        IsBorderRequired = false,
        IsVideoFramePreviewEnabled = true,
        VideoFramePreviewSize = new ScreenSize(PreviewEdgePx, PreviewEdgePx),
    };

    /// <summary>Assemble the full <see cref="RecorderOptions"/> for one recording.</summary>
    public static RecorderOptions Build(WindowRecordingSource source, VideoOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        return new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = { source } },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                // Record at the window's native size; the mux/library stage owns any downscale.
                OutputFrameSize = ScreenSize.Empty,
                Stretch = StretchMode.Uniform,
            },
            // Audio is captured and muxed by a separate engine; never write an audio track here.
            AudioOptions = new AudioOptions { IsAudioEnabled = false },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder
                {
                    BitrateMode = H264BitrateControlMode.CBR,
                    EncoderProfile = H264Profile.High,
                },
                Bitrate = options.BitrateMbps * 1_000_000, // library wants bits per second
                Framerate = options.Fps,
                IsFixedFramerate = true,          // deterministic cadence for VOD scrubbing
                IsHardwareEncodingEnabled = true, // NVENC/AMF/QSV auto-selected by Media Foundation
                IsFragmentedMp4Enabled = options.FragmentedMp4,
                IsMp4FastStartEnabled = false,    // fast-start rewrites moov at the end; skip for crash-safety
                IsLowLatencyEnabled = false,
                IsThrottlingDisabled = false,
            },
        };
    }

    /// <summary>Ensure the directory holding <paramref name="stagingMp4Path"/> exists.</summary>
    public static void EnsureStagingDirectory(string stagingMp4Path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingMp4Path);

        var dir = Path.GetDirectoryName(Path.GetFullPath(stagingMp4Path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}
