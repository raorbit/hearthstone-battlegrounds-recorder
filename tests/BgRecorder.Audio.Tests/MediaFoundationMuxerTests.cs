using System.Diagnostics;
using System.Text.Json;
using BgRecorder.Audio.Muxing;

namespace BgRecorder.Audio.Tests;

/// <summary>
/// Integration tests for the finalize muxer against the real Spike B/D scratchpad
/// assets. Verified with ffprobe — a verification-only tool that is never a runtime
/// dependency of the shipped app.
/// </summary>
public class MediaFoundationMuxerTests
{
    // Real M2 test assets staged by the earlier spikes.
    private static readonly string Scratchpad =
        Environment.GetEnvironmentVariable("BG_SCRATCHPAD")
        ?? @"C:\Users\raorb\AppData\Local\Temp\claude\C--Users-raorb-Projects-hearthstone-battlegrounds-recorder\4db57b15-f4e1-4676-b4f3-2e908ff61ea9\scratchpad";

    private static string VideoAsset => Path.Combine(Scratchpad, "spikeB-capture.mp4");
    private static string AudioAsset => Path.Combine(Scratchpad, "spikeD-hearthstone.wav");

    [Fact]
    public async Task Mux_ZeroOffset_ProducesOneH264AndOneAacStream()
    {
        if (!PrerequisitesPresent())
            return;

        string output = Path.Combine(Scratchpad, "muxtest-offset0.mp4");
        TryDelete(output);

        await new MediaFoundationMuxer().MuxAsync(VideoAsset, AudioAsset, TimeSpan.Zero, output, CancellationToken.None);

        var probe = Ffprobe(output);
        var video = probe.Streams.Where(s => s.CodecType == "video").ToList();
        var audio = probe.Streams.Where(s => s.CodecType == "audio").ToList();

        Assert.Single(video);
        Assert.Equal("h264", video[0].CodecName);
        Assert.Single(audio);
        Assert.Equal("aac", audio[0].CodecName);

        // Container duration is driven by the ~14.5 s video.
        Assert.InRange(probe.FormatDuration, 13.5, 16.0);

        // Audio (~10 s WAV) sits inside the video and starts at the top.
        Assert.InRange(audio[0].Duration, 9.0, 11.0);
    }

    [Fact]
    public async Task Mux_PositiveOffset_DelaysAudioByLeadingSilence()
    {
        if (!PrerequisitesPresent())
            return;

        string zeroOut = Path.Combine(Scratchpad, "muxtest-offset0.mp4");
        string offsetOut = Path.Combine(Scratchpad, "muxtest-offset1500ms.mp4");
        TryDelete(zeroOut);
        TryDelete(offsetOut);

        var muxer = new MediaFoundationMuxer();
        var offset = TimeSpan.FromMilliseconds(1500);

        await muxer.MuxAsync(VideoAsset, AudioAsset, TimeSpan.Zero, zeroOut, CancellationToken.None);
        await muxer.MuxAsync(VideoAsset, AudioAsset, offset, offsetOut, CancellationToken.None);

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

    [Fact]
    public async Task Mux_EmptyAudioPath_ProducesVideoOnlyOutput()
    {
        // The session convention (SessionCoordinator/StartupRecovery): an empty audio path
        // means the audio capture died or never started — remux the video alone.
        if (!File.Exists(VideoAsset) || !FfprobeAvailable())
            return;

        string output = Path.Combine(Scratchpad, "muxtest-videoonly.mp4");
        TryDelete(output);

        await new MediaFoundationMuxer().MuxAsync(VideoAsset, string.Empty, TimeSpan.Zero, output, CancellationToken.None);

        var probe = Ffprobe(output);
        var video = Assert.Single(probe.Streams, s => s.CodecType == "video");
        Assert.Equal("h264", video.CodecName);
        Assert.DoesNotContain(probe.Streams, s => s.CodecType == "audio");
        Assert.InRange(probe.FormatDuration, 13.5, 16.0);
    }

    private static bool PrerequisitesPresent()
        => File.Exists(VideoAsset) && File.Exists(AudioAsset) && FfprobeAvailable();

    private static bool FfprobeAvailable()
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private sealed record ProbeStream(string CodecType, string CodecName, double Duration);
    private sealed record ProbeResult(List<ProbeStream> Streams, double FormatDuration);
}
