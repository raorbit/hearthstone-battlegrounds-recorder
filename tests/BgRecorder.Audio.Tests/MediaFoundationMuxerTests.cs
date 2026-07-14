using System.Diagnostics;
using System.Text.Json;
using BgRecorder.Audio.Muxing;
using NAudio.Wave;

namespace BgRecorder.Audio.Tests;

/// <summary>
/// Integration tests for the finalize muxer. They need a real H.264 MP4 to remux and ffprobe to
/// verify the output; the audio WAV is synthesized in-test. When the video fixture or ffprobe is
/// missing the tests report a VISIBLE SKIP (via <see cref="MuxerAssetsFactAttribute"/>) rather than
/// a silent pass, so the only coverage of the hand-rolled MF/COM muxer can never go falsely green.
///
/// Supply the video fixture by either setting BG_SCRATCHPAD to a folder containing
/// <c>spikeB-capture.mp4</c>, or placing an H.264 MP4 at
/// <c>tests/BgRecorder.Audio.Tests/fixtures/sample-h264.mp4</c>.
/// </summary>
public class MediaFoundationMuxerTests : IDisposable
{
    private readonly string _dir;

    public MediaFoundationMuxerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bg-muxer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [MuxerAssetsFact]
    public async Task Mux_ZeroOffset_ProducesOneH264AndOneAacStream()
    {
        string video = MuxerAssets.ResolveVideo()!;
        string audio = WriteSyntheticWav(Path.Combine(_dir, "audio.wav"), seconds: 10.0);
        string output = Path.Combine(_dir, "muxtest-offset0.mp4");

        await new MediaFoundationMuxer().MuxAsync(video, audio, TimeSpan.Zero, output, CancellationToken.None);

        var probe = Ffprobe(output);
        var videoStreams = probe.Streams.Where(s => s.CodecType == "video").ToList();
        var audioStreams = probe.Streams.Where(s => s.CodecType == "audio").ToList();

        Assert.Single(videoStreams);
        Assert.Equal("h264", videoStreams[0].CodecName);
        Assert.Single(audioStreams);
        Assert.Equal("aac", audioStreams[0].CodecName);

        // Container duration is driven by the ~14.5 s video.
        Assert.InRange(probe.FormatDuration, 13.5, 16.0);

        // Audio (10 s WAV) sits inside the video and starts at the top.
        Assert.InRange(audioStreams[0].Duration, 9.0, 11.0);
    }

    [MuxerAssetsFact]
    public async Task Mux_PositiveOffset_DelaysAudioByLeadingSilence()
    {
        string video = MuxerAssets.ResolveVideo()!;
        string audio = WriteSyntheticWav(Path.Combine(_dir, "audio.wav"), seconds: 10.0);
        string zeroOut = Path.Combine(_dir, "muxtest-offset0.mp4");
        string offsetOut = Path.Combine(_dir, "muxtest-offset1500ms.mp4");

        var muxer = new MediaFoundationMuxer();
        var offset = TimeSpan.FromMilliseconds(1500);

        await muxer.MuxAsync(video, audio, TimeSpan.Zero, zeroOut, CancellationToken.None);
        await muxer.MuxAsync(video, audio, offset, offsetOut, CancellationToken.None);

        var zeroProbe = Ffprobe(zeroOut);
        var offsetProbe = Ffprobe(offsetOut);

        // Still exactly one h264 + one aac stream after applying the offset.
        Assert.Single(offsetProbe.Streams, s => s.CodecType == "video");
        var offsetAudio = Assert.Single(offsetProbe.Streams, s => s.CodecType == "audio");
        var zeroAudio = Assert.Single(zeroProbe.Streams, s => s.CodecType == "audio");

        Assert.Equal("aac", offsetAudio.CodecName);

        // Leading-silence design: the audio stream is ~1.5 s longer than the un-offset mux.
        double delta = offsetAudio.Duration - zeroAudio.Duration;
        Assert.InRange(delta, 1.2, 1.8);

        // Container is still bounded by the video length.
        Assert.InRange(offsetProbe.FormatDuration, 13.5, 16.0);
    }

    [MuxerAssetsFact]
    public async Task Mux_NegativeOffset_KeepsAudioStartingNearZeroWithoutDroppingABuffer()
    {
        string video = MuxerAssets.ResolveVideo()!;
        string audio = WriteSyntheticWav(Path.Combine(_dir, "audio.wav"), seconds: 10.0);
        string zeroOut = Path.Combine(_dir, "muxtest-offset0.mp4");
        string negOut = Path.Combine(_dir, "muxtest-negoffset.mp4");

        var muxer = new MediaFoundationMuxer();
        // A small negative offset is the common case (audioClock - videoClock). The muxer must trim
        // only the sub-zero slice of the first buffer, not drop the whole buffer — so the audio
        // stays essentially as long as the un-offset mux (no leading buffer-sized gap / loss).
        var offset = TimeSpan.FromMilliseconds(-40);

        await muxer.MuxAsync(video, audio, TimeSpan.Zero, zeroOut, CancellationToken.None);
        await muxer.MuxAsync(video, audio, offset, negOut, CancellationToken.None);

        var zeroAudio = Assert.Single(Ffprobe(zeroOut).Streams, s => s.CodecType == "audio");
        var negAudio = Assert.Single(Ffprobe(negOut).Streams, s => s.CodecType == "audio");

        Assert.Equal("aac", negAudio.CodecName);

        // Only ~40 ms trimmed, not a whole reader buffer: durations stay within ~150 ms.
        double delta = Math.Abs(zeroAudio.Duration - negAudio.Duration);
        Assert.True(delta < 0.15, $"negative-offset audio lost {delta:0.###}s vs zero-offset; a dropped buffer would be far larger");
    }

    [MuxerAssetsFact]
    public async Task Mux_EmptyAudioPath_ProducesVideoOnlyOutput()
    {
        // The session convention (SessionCoordinator/StartupRecovery): an empty audio path
        // means the audio capture died or never started — remux the video alone.
        string video = MuxerAssets.ResolveVideo()!;
        string output = Path.Combine(_dir, "muxtest-videoonly.mp4");

        await new MediaFoundationMuxer().MuxAsync(video, string.Empty, TimeSpan.Zero, output, CancellationToken.None);

        var probe = Ffprobe(output);
        var videoStream = Assert.Single(probe.Streams, s => s.CodecType == "video");
        Assert.Equal("h264", videoStream.CodecName);
        Assert.DoesNotContain(probe.Streams, s => s.CodecType == "audio");
        Assert.InRange(probe.FormatDuration, 13.5, 16.0);
    }

    private static string WriteSyntheticWav(string path, double seconds)
    {
        // Canonical 44.1 kHz / 16-bit / stereo PCM, a quiet 440 Hz tone (non-silent so the AAC
        // encoder has real signal). The muxer resamples/encodes this to AAC.
        var format = new WaveFormat(44100, 16, 2);
        using var writer = new WaveFileWriter(path, format);
        int frames = (int)(format.SampleRate * seconds);
        var frame = new byte[format.Channels * 2];
        for (int i = 0; i < frames; i++)
        {
            short s = (short)(3000 * Math.Sin(2 * Math.PI * 440 * i / format.SampleRate));
            frame[0] = (byte)(s & 0xFF);
            frame[1] = (byte)((s >> 8) & 0xFF);
            frame[2] = frame[0];
            frame[3] = frame[1];
            writer.Write(frame, 0, frame.Length);
        }
        return path;
    }

    private static ProbeResult Ffprobe(string file)
    {
        var psi = new ProcessStartInfo("ffprobe",
            $"-v error -print_format json -show_streams -show_format \"{file}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        string json = p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        Assert.True(p.ExitCode == 0, $"ffprobe failed: {err}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var streams = new List<ProbeStream>();
        foreach (var s in root.GetProperty("streams").EnumerateArray())
        {
            streams.Add(new ProbeStream(
                s.TryGetProperty("codec_type", out var ct) ? ct.GetString() ?? "" : "",
                s.TryGetProperty("codec_name", out var cn) ? cn.GetString() ?? "" : "",
                s.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var dv) ? dv : 0.0));
        }

        double formatDuration = 0.0;
        if (root.GetProperty("format").TryGetProperty("duration", out var fd))
            double.TryParse(fd.GetString(), System.Globalization.CultureInfo.InvariantCulture, out formatDuration);

        return new ProbeResult(streams, formatDuration);
    }

    private sealed record ProbeStream(string CodecType, string CodecName, double Duration);
    private sealed record ProbeResult(List<ProbeStream> Streams, double FormatDuration);
}

/// <summary>
/// Skips (visibly) unless the muxer's real-asset prerequisites are present: an H.264 video fixture
/// to remux and ffprobe to verify the result. Mirrors the repo's <c>HearthstoneFact</c> pattern so a
/// missing prerequisite is a reported SKIP, never a false pass.
/// </summary>
public sealed class MuxerAssetsFactAttribute : FactAttribute
{
    public MuxerAssetsFactAttribute()
    {
        if (MuxerAssets.ResolveVideo() is null)
            Skip = "Muxer video fixture not found. Set BG_SCRATCHPAD to a folder with spikeB-capture.mp4, "
                 + "or add tests/BgRecorder.Audio.Tests/fixtures/sample-h264.mp4 (a real H.264 MP4 to remux).";
        else if (!MuxerAssets.FfprobeAvailable())
            Skip = "ffprobe is not on PATH; the muxer integration tests verify output streams with ffprobe.";
    }
}

/// <summary>Resolves muxer test assets without any hardcoded per-session scratchpad path.</summary>
internal static class MuxerAssets
{
    /// <summary>
    /// Locates the H.264 video fixture: BG_SCRATCHPAD/spikeB-capture.mp4 first (locally staged
    /// spike asset), then a repo-relative fixtures file. Returns null if neither exists.
    /// </summary>
    public static string? ResolveVideo()
    {
        var scratch = Environment.GetEnvironmentVariable("BG_SCRATCHPAD");
        if (!string.IsNullOrEmpty(scratch))
        {
            string staged = Path.Combine(scratch, "spikeB-capture.mp4");
            if (File.Exists(staged))
                return staged;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "BgRecorder.Audio.Tests", "fixtures", "sample-h264.mp4");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public static bool FfprobeAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffprobe", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
