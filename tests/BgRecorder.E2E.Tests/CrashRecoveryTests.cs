using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BgRecorder.Audio.Muxing;
using BgRecorder.Core;
using BgRecorder.Core.Data;
using BgRecorder.Data;
using BgRecorder.Session;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BgRecorder.E2E.Tests;

/// <summary>
/// The M2 crash-recovery exit gate: a <c>kill -9</c> during a live recording must leave a playable
/// partial VOD that the next launch registers as <see cref="VideoStatus.Incomplete"/>.
///
/// A child process (<c>BgRecorder.CrashHarness</c>) runs the REAL recording stack — real log tail +
/// parse + <see cref="SessionCoordinator"/> + ScreenRecorderLib capturing the live Hearthstone window
/// + process-loopback audio — into a sandbox staging dir, fed sanitized fixture match-03 until it is
/// mid-match. Once its staged fragmented MP4 is verifiably growing, the test hard-kills ONLY that child
/// (<see cref="Process.Kill(bool)"/> with <c>entireProcessTree: false</c>, after checking the PID is the
/// one it spawned), then runs <see cref="StartupRecovery"/> against the sandbox and a fresh temp DB. The
/// recovered library MP4 must exist, ffprobe must report a playable H.264 stream with a positive
/// duration (a fragmented MP4 survives the kill), the match row must be Incomplete, and staging must be
/// cleaned up. The game is only ever read (window-captured); nothing is ever written to or signalled at it.
/// </summary>
[Trait("Category", "Crash")]
public sealed class CrashRecoveryTests : IDisposable
{
    private readonly string _root;
    private readonly string _install;
    private readonly string _staging;
    private readonly string _library;
    private readonly string _recoveryDbPath;

    public CrashRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "bgrec-crash-recovery-" + Guid.NewGuid().ToString("N"));
        _install = Path.Combine(_root, "install");
        _staging = Path.Combine(_root, "staging");
        _library = Path.Combine(_root, "library");
        _recoveryDbPath = Path.Combine(_root, "recovery.db");
        Directory.CreateDirectory(_install);
        Directory.CreateDirectory(_staging);
        Directory.CreateDirectory(_library);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [HearthstoneFact]
    public async Task Kill9_mid_recording_leaves_a_playable_partial_registered_incomplete()
    {
        string harnessDll = LocateHarnessAssembly();

        var stdout = new BlockingCollection<string>();
        using var child = StartHarness(harnessDll, _root, stdout);
        try
        {
            // --- Wait for the child to reach a live, growing mid-recording. ---
            string signal = AwaitSignal(stdout, child, TimeSpan.FromSeconds(90));
            Assert.True(signal == "RECORDING",
                $"Harness did not reach a live recording (last signal '{signal}'). Output:\n{DumpEarlyOutput(stdout)}");

            string stagedVideo = await AwaitGrowingStagedVideoAsync(TimeSpan.FromSeconds(30));
            Assert.True(File.Exists(ManifestPathFor(stagedVideo)),
                "A crash-recovery manifest must exist next to the staged video before the kill.");

            // --- Hard-kill ONLY our own child; verify the PID belongs to the harness we launched. ---
            child.Refresh();
            Assert.False(child.HasExited, "The harness exited before it could be killed mid-recording.");
            int killedPid = child.Id;
            Assert.True(
                string.Equals(child.ProcessName, "dotnet", StringComparison.OrdinalIgnoreCase),
                $"Refusing to kill PID {killedPid}: process name '{child.ProcessName}' is not the dotnet host of our spawned harness.");

            child.Kill(entireProcessTree: false); // SIGKILL-equivalent: TerminateProcess, no graceful finalize
            Assert.True(child.WaitForExit(15000), "The killed harness did not exit within 15s.");
        }
        finally
        {
            // Never leave a runaway recorder holding the live game window if an assertion above threw.
            TryKill(child);
        }

        // The kill left an orphaned fragmented MP4 + un-finalized manifest in staging. Nothing else exists yet.
        Assert.NotEmpty(Directory.GetDirectories(_staging));

        // --- Startup recovery over the sandbox, into a fresh temp DB (as a next launch would do). ---
        var settings = new AppSettings
        {
            HearthstoneInstallDir = _install,
            LibraryDir = _library,
            StagingDir = _staging,
        };
        IMatchRepository repository = new SqliteMatchRepository(_recoveryDbPath);
        await repository.InitializeAsync();

        var recovery = new StartupRecovery(new MediaFoundationMuxer(), new MatchAssembler(), repository, settings);
        var report = await recovery.RunAsync();

        var session = Assert.Single(report.Sessions);
        Assert.True(session.Outcome == RecoveryOutcome.Recovered,
            $"Recovery did not reclaim the crashed session (outcome {session.Outcome}): {session.Detail}");

        // --- The recovered row: Incomplete, backed by a real on-disk library file. ---
        var match = Assert.Single(await repository.ListMatchesAsync());
        Assert.Equal(VideoStatus.Incomplete, match.VideoStatus);
        Assert.NotNull(match.VideoPath);
        Assert.True(File.Exists(match.VideoPath), $"Recovered video is missing on disk: {match.VideoPath}");
        Assert.StartsWith(_library, Path.GetFullPath(match.VideoPath!), StringComparison.OrdinalIgnoreCase);
        Assert.True(match.VideoSizeBytes is > 0, "Recovered video must have a non-zero size.");

        // Exactly one library MP4, and it is the recovered one.
        var libraryMp4 = Assert.Single(Directory.GetFiles(_library, "*.mp4"));
        Assert.Equal(Path.GetFullPath(match.VideoPath!), Path.GetFullPath(libraryMp4));

        // --- Playability: a fragmented MP4 remains a valid H.264 file with a positive duration despite the kill. ---
        var probe = Ffprobe(libraryMp4);
        var video = Assert.Single(probe.Streams, s => s.CodecType == "video");
        Assert.Equal("h264", video.CodecName);
        Assert.True(probe.FormatDurationSeconds > 0,
            $"Recovered VOD must have a positive playable duration; ffprobe reported {probe.FormatDurationSeconds}s.");

        // --- Staging cleaned up: the recovered session folder is gone. ---
        Assert.Empty(Directory.GetDirectories(_staging));
    }

    // ---------------------------------------------------------------- harness process

    private static Process StartHarness(string harnessDll, string sandbox, BlockingCollection<string> stdout)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(harnessDll);
        psi.ArgumentList.Add(sandbox);

        var child = new Process { StartInfo = psi, EnableRaisingEvents = true };
        child.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.Add(e.Data); };
        child.ErrorDataReceived += (_, e) => { if (e.Data is not null) stdout.Add("STDERR " + e.Data); };
        child.Start();
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();
        return child;
    }

    /// <summary>
    /// Blocks until the harness prints a terminal protocol token (RECORDING or a failure token) or exits.
    /// Returns the token seen; "EXITED" if the child died first; "TIMEOUT" if neither happened in time.
    /// </summary>
    private static string AwaitSignal(BlockingCollection<string> stdout, Process child, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (stdout.TryTake(out var line, 250))
            {
                if (line is "RECORDING" or "NO_GAME" or "NO_DISCOVERY" or "NO_RECORDING")
                    return line;
                // TICK/DIAG/USAGE/STDERR lines are progress noise; keep waiting.
            }
            else if (child.HasExited && stdout.Count == 0)
            {
                return "EXITED";
            }
        }
        return "TIMEOUT";
    }

    private static string DumpEarlyOutput(BlockingCollection<string> stdout)
    {
        var lines = new List<string>();
        while (lines.Count < 40 && stdout.TryTake(out var line, 50))
            lines.Add(line);
        return lines.Count == 0 ? "(no output captured)" : string.Join("\n", lines);
    }

    private async Task<string> AwaitGrowingStagedVideoAsync(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            string? video = Directory
                .EnumerateFiles(_staging, "video.mp4", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (video is not null)
            {
                long first = SafeLength(video);
                await Task.Delay(750);
                long second = SafeLength(video);
                if (first > 0 && second > first)
                    return video;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException(
            $"No growing staged video.mp4 appeared under '{_staging}' within {timeout}.");
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static string ManifestPathFor(string stagedVideo)
        => Path.Combine(Path.GetDirectoryName(stagedVideo)!, ManifestStore.FileName);

    private static void TryKill(Process child)
    {
        try
        {
            child.Refresh();
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: false);
                child.WaitForExit(10000);
            }
        }
        catch { /* already gone */ }
    }

    /// <summary>
    /// Finds the built <c>BgRecorder.CrashHarness.dll</c>. The E2E project references the harness with
    /// ReferenceOutputAssembly=false, so building the tests also builds the harness into its own bin;
    /// we pick the newest matching assembly (covers isolated per-agent output paths).
    /// </summary>
    private static string LocateHarnessAssembly()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var harnessProject = Path.Combine(dir.FullName, "tests", "BgRecorder.CrashHarness");
            if (Directory.Exists(harnessProject))
            {
                var binRoot = Path.Combine(harnessProject, "bin");
                if (Directory.Exists(binRoot))
                {
                    var newest = Directory
                        .EnumerateFiles(binRoot, "BgRecorder.CrashHarness.dll", SearchOption.AllDirectories)
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .FirstOrDefault();
                    if (newest is not null)
                        return newest.FullName;
                }
                throw new FileNotFoundException(
                    $"BgRecorder.CrashHarness.dll not found under '{binRoot}'. Build the tests (which build the harness) first.");
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the tests/BgRecorder.CrashHarness project above " + AppContext.BaseDirectory);
    }

    // ---------------------------------------------------------------- ffprobe (verification only)

    /// <summary>ffprobe is verification-only tooling for this test; the app itself never invokes ffmpeg.</summary>
    private static ProbeResult Ffprobe(string file)
    {
        var psi = new ProcessStartInfo("ffprobe", $"-v error -print_format json -show_streams -show_format \"{file}\"")
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
        var streams = new List<ProbeStream>();
        foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            streams.Add(new ProbeStream(
                s.TryGetProperty("codec_type", out var ct) ? ct.GetString() ?? "" : "",
                s.TryGetProperty("codec_name", out var cn) ? cn.GetString() ?? "" : ""));
        }

        double duration = 0;
        if (doc.RootElement.TryGetProperty("format", out var fmt)
            && fmt.TryGetProperty("duration", out var dur)
            && dur.ValueKind == JsonValueKind.String
            && double.TryParse(dur.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            duration = parsed;
        }
        return new ProbeResult(streams, duration);
    }

    private sealed record ProbeStream(string CodecType, string CodecName);

    private sealed record ProbeResult(List<ProbeStream> Streams, double FormatDurationSeconds);
}
