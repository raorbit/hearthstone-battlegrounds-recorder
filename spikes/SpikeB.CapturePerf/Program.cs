using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ScreenRecorderLib;

// Spike B - capture performance.
// Records a target window (default: Hearthstone) with ScreenRecorderLib using a
// hardware H.264 encoder (NVENC when available), audio disabled, into an .mp4.
// Salvage target: docs/implementation-plan.md -> CaptureEngine / IRecorder.
//
// This file is deliberately a single, readable unit. The RecordingConfig record and
// the CaptureSession class are the parts meant to be lifted into the real CaptureEngine;
// the ffprobe / Main plumbing is spike scaffolding.

var config = RecordingConfig.Parse(args);
if (config is null)
{
    RecordingConfig.PrintUsage();
    return 2;
}

Log($"config: window~=\"{config.WindowSubstring}\" seconds={config.Seconds} fps={config.Fps} " +
    $"bitrate={config.BitrateMbps}Mbps out=\"{config.OutputPath}\" fragmentedMp4={config.FragmentedMp4} " +
    $"frameSize={(config.ForceFrameSize ? $"{config.Width}x{config.Height}" : "native")}");

var session = new CaptureSession(config);
int exit;
try
{
    exit = await session.RunAsync();
}
catch (Exception ex)
{
    Log($"FATAL: {ex.GetType().Name}: {ex.Message}");
    Log(ex.ToString());
    return 1;
}

if (exit == 0)
    Probe(config.OutputPath);

return exit;

static void Log(string msg) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");

// Best-effort duration/codec probe via ffprobe if it is on PATH. Not required for the
// spike to pass: the OnRecordingComplete event + file-size check is the primary gate.
static void Probe(string path)
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffprobe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams");
        psi.ArgumentList.Add("v:0");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("format=duration,format_name:stream=codec_name,width,height,avg_frame_rate,nb_frames");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("default=noprint_wrappers=1");
        psi.ArgumentList.Add(path);

        using var p = Process.Start(psi);
        if (p is null)
        {
            Log("ffprobe: could not start (skipping duration probe)");
            return;
        }
        string outp = p.StandardOutput.ReadToEnd();
        string errp = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        Log("ffprobe results:");
        foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Log($"  {line}");
        if (!string.IsNullOrWhiteSpace(errp))
            Log($"  ffprobe stderr: {errp.Trim()}");
    }
    catch (Exception ex)
    {
        // ffprobe not installed / not on PATH: acceptable per the spike brief.
        Log($"ffprobe unavailable ({ex.GetType().Name}: {ex.Message}); relying on completion event + file size.");
    }
}

/// <summary>Parsed command-line options for one capture run.</summary>
sealed record RecordingConfig(
    string WindowSubstring,
    int Seconds,
    int Fps,
    int BitrateMbps,
    string OutputPath,
    bool FragmentedMp4,
    bool ForceFrameSize,
    int Width,
    int Height)
{
    public static RecordingConfig? Parse(string[] args)
    {
        string window = "Hearthstone";
        int seconds = 30;
        int fps = 60;
        int bitrate = 12;
        int width = 1920;
        int height = 1080;
        bool fragmented = false;
        bool forceFrameSize = true;

        string defaultOut = Path.Combine(
            Environment.GetEnvironmentVariable("SPIKEB_OUT_DIR") ?? Directory.GetCurrentDirectory(),
            "spikeB-capture.mp4");
        string outPath = defaultOut;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next(string name)
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"missing value for {name}");
                return args[++i];
            }

            try
            {
                switch (a.ToLowerInvariant())
                {
                    case "--window": window = Next(a); break;
                    case "--seconds": seconds = int.Parse(Next(a), CultureInfo.InvariantCulture); break;
                    case "--fps": fps = int.Parse(Next(a), CultureInfo.InvariantCulture); break;
                    case "--bitratembps": bitrate = int.Parse(Next(a), CultureInfo.InvariantCulture); break;
                    case "--out": outPath = Next(a); break;
                    case "--width": width = int.Parse(Next(a), CultureInfo.InvariantCulture); break;
                    case "--height": height = int.Parse(Next(a), CultureInfo.InvariantCulture); break;
                    case "--fragmented": fragmented = true; break;
                    case "--native": forceFrameSize = false; break;
                    case "-h":
                    case "--help":
                        return null;
                    default:
                        Console.Error.WriteLine($"unknown argument: {a}");
                        return null;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"bad argument {a}: {ex.Message}");
                return null;
            }
        }

        if (width <= 0 || height <= 0)
            forceFrameSize = false;

        return new RecordingConfig(window, seconds, fps, bitrate, Path.GetFullPath(outPath),
            fragmented, forceFrameSize, width, height);
    }

    public static void PrintUsage()
    {
        Console.WriteLine(
            "Usage: SpikeB.CapturePerf [options]\n" +
            "  --window <substr>     window title substring to match (default: Hearthstone)\n" +
            "  --seconds <n>         capture duration in seconds (default: 30)\n" +
            "  --fps <n>             target framerate (default: 60)\n" +
            "  --bitrateMbps <n>     H.264 bitrate in Mbps, CBR (default: 12)\n" +
            "  --out <path>          output .mp4 path (default: ./spikeB-capture.mp4 or $SPIKEB_OUT_DIR)\n" +
            "  --width <n>           forced output width  (default: 1920)\n" +
            "  --height <n>          forced output height (default: 1080)\n" +
            "  --native              record at the window's native size (skip forced 1080p scaling)\n" +
            "  --fragmented          enable fragmented MP4 (crash-safe blocks; M2 requirement)\n");
    }
}

/// <summary>
/// Picks the window to record. Both the game and any running deck-tracker overlay have
/// titles containing "Hearthstone", so a naive substring match grabs the wrong (tiny,
/// transparent) overlay window. This resolver prefers the *game process's* main window.
/// Salvage target: the real CaptureEngine wants exactly this - bind to the game window,
/// not whatever titled window happens to sort first.
/// </summary>
static class WindowResolver
{
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static RecordableWindow? Resolve(
        List<RecordableWindow> windows, string substring, Action<string> log)
    {
        // Map each recordable window to its owning process id.
        static uint OwningPid(IntPtr h) => GetWindowThreadProcessId(h, out uint pid) == 0 ? 0 : pid;

        // Processes whose name contains the substring, plus their main-window handles.
        var procs = Process.GetProcesses();
        var exactPids = new HashSet<int>();
        var fuzzyPids = new HashSet<int>();
        var exactMainHandles = new HashSet<long>();
        var fuzzyMainHandles = new HashSet<long>();
        foreach (var p in procs)
        {
            string name;
            IntPtr main;
            try { name = p.ProcessName; main = p.MainWindowHandle; }
            catch { continue; }
            bool exact = string.Equals(name, substring, StringComparison.OrdinalIgnoreCase);
            bool fuzzy = name.Contains(substring, StringComparison.OrdinalIgnoreCase);
            if (!fuzzy) continue;
            (exact ? exactPids : fuzzyPids).Add(p.Id);
            if (main != IntPtr.Zero)
                (exact ? exactMainHandles : fuzzyMainHandles).Add(main.ToInt64());
        }

        var candidates = windows
            .Where(w => !string.IsNullOrWhiteSpace(w.Title)
                        && w.IsValidWindow()
                        && w.Title.Contains(substring, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            log($"No recordable window title contains \"{substring}\". All titled windows:");
            foreach (var w in windows.Where(w => !string.IsNullOrWhiteSpace(w.Title))
                                     .OrderBy(w => w.Title, StringComparer.OrdinalIgnoreCase))
                log($"  \"{w.Title}\"  handle=0x{w.Handle.ToInt64():X}  api={w.RecorderApi}  valid={w.IsValidWindow()}");
            if (!windows.Any(w => !string.IsNullOrWhiteSpace(w.Title)))
                log("  (none - no titled windows were reported)");
            return null;
        }

        int Score(RecordableWindow w)
        {
            long h = w.Handle.ToInt64();
            uint pid = OwningPid(w.Handle);
            int s = 0;
            if (exactMainHandles.Contains(h)) s += 1000;   // main window of the exact-named process = the game
            else if (fuzzyMainHandles.Contains(h)) s += 500;
            if (exactPids.Contains((int)pid)) s += 200;     // owned by the exact-named process
            else if (fuzzyPids.Contains((int)pid)) s += 100;
            if (string.Equals(w.Title, substring, StringComparison.OrdinalIgnoreCase)) s += 50;
            else if (w.Title.StartsWith(substring, StringComparison.OrdinalIgnoreCase)) s += 10;
            if (w.IsMinmimized()) s -= 2000;                // never prefer a minimized window
            return s;
        }

        var ranked = candidates
            .Select(w => (w, score: Score(w)))
            .OrderByDescending(t => t.score)
            .ThenBy(t => t.w.Title.Length)                  // "Hearthstone" beats "HearthstoneOverlay"
            .ToList();

        log("candidate windows (scored):");
        foreach (var (w, score) in ranked)
        {
            uint pid = OwningPid(w.Handle);
            string owner = "?";
            try { owner = Process.GetProcessById((int)pid).ProcessName; } catch { }
            log($"  score={score,5}  \"{w.Title}\"  handle=0x{w.Handle.ToInt64():X}  pid={pid} ({owner})  api={w.RecorderApi}");
        }

        var pick = ranked[0].w;
        if (ranked[0].score <= 0)
            log("WARNING: best candidate scored <= 0 (no game-process match); recording it anyway.");
        return pick;
    }
}

/// <summary>
/// Owns one ScreenRecorderLib recording of a single window. This is the piece meant to
/// migrate into the real CaptureEngine behind IRecorder.
/// </summary>
sealed class CaptureSession(RecordingConfig config)
{
    private readonly RecordingConfig _config = config;
    private readonly TaskCompletionSource<string> _completed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile string? _failure;

    private static void Log(string msg) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");

    public async Task<int> RunAsync()
    {
        // 1. Enumerate recordable windows and pick the match.
        List<RecordableWindow> windows;
        try
        {
            windows = Recorder.GetWindows();
        }
        catch (Exception ex)
        {
            Log($"Recorder.GetWindows() threw: {ex.GetType().Name}: {ex.Message}");
            Log("This usually means the ScreenRecorderLib native DLL failed to load " +
                "(architecture/runtime mismatch). See README load-notes.");
            throw;
        }

        var match = WindowResolver.Resolve(windows, _config.WindowSubstring, Log);
        if (match is null)
            return 3;

        if (match.IsMinmimized())
            Log($"WARNING: target window \"{match.Title}\" is minimized - capture may be black frames.");

        Log($"target window: \"{match.Title}\"  handle=0x{match.Handle.ToInt64():X}  api={match.RecorderApi}");

        // 2. Build recorder options.
        var source = new WindowRecordingSource(match.Handle)
        {
            IsCursorCaptureEnabled = false,
        };

        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = { source } },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                Stretch = StretchMode.Uniform,
                OutputFrameSize = _config.ForceFrameSize
                    ? new ScreenSize(_config.Width, _config.Height)
                    : ScreenSize.Empty,
            },
            // Audio is Spike D's job - explicitly disabled here.
            AudioOptions = new AudioOptions { IsAudioEnabled = false },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder
                {
                    BitrateMode = H264BitrateControlMode.CBR,
                    EncoderProfile = H264Profile.High,
                },
                Bitrate = _config.BitrateMbps * 1_000_000, // library wants bits per second
                Framerate = _config.Fps,
                IsFixedFramerate = true,       // deterministic 60fps output for VOD scrubbing
                IsHardwareEncodingEnabled = true, // NVENC/AMF/QSV auto-selected by Media Foundation
                IsFragmentedMp4Enabled = _config.FragmentedMp4,
                IsMp4FastStartEnabled = false, // fast-start rewrites moov at the end; skip for crash-safety
                IsLowLatencyEnabled = false,
                IsThrottlingDisabled = false,
            },
        };

        Log("encoder request: H264 CBR profile=High, IsHardwareEncodingEnabled=true " +
            "(Media Foundation picks the concrete HW encoder; the library does not expose which one - " +
            "verify NVENC via nvidia-smi encoder utilization during capture).");

        Directory.CreateDirectory(Path.GetDirectoryName(_config.OutputPath)!);

        // 3. Wire events.
        using var rec = Recorder.CreateRecorder(options);
        rec.OnStatusChanged += (_, e) => Log($"status: {e.Status}");
        rec.OnRecordingComplete += (_, e) =>
        {
            Log($"OnRecordingComplete: {e.FilePath}");
            _completed.TrySetResult(e.FilePath);
        };
        rec.OnRecordingFailed += (_, e) =>
        {
            Log($"OnRecordingFailed: {e.Error}");
            _failure = e.Error;
            _completed.TrySetResult(string.Empty);
        };

        // 4. Record for the requested duration, then stop and wait for finalization.
        Log($"recording -> {_config.OutputPath}");
        rec.Record(_config.OutputPath);

        await Task.Delay(TimeSpan.FromSeconds(_config.Seconds));

        Log("stopping...");
        rec.Stop();

        // OnRecordingComplete fires after MF finalizes the container.
        var finalize = await Task.WhenAny(_completed.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (finalize != _completed.Task)
        {
            Log("ERROR: timed out (30s) waiting for OnRecordingComplete after Stop().");
            return 4;
        }

        if (_failure is not null)
        {
            Log($"ERROR: recording failed: {_failure}");
            return 5;
        }

        // 5. Verify output.
        var fi = new FileInfo(_config.OutputPath);
        if (!fi.Exists)
        {
            Log($"ERROR: output file does not exist: {_config.OutputPath}");
            return 6;
        }
        double mb = fi.Length / (1024.0 * 1024.0);
        Log($"output file: {_config.OutputPath}  size={fi.Length:N0} bytes ({mb:F2} MB)");
        if (fi.Length < 1_048_576)
        {
            Log("ERROR: output file is <= 1 MB - treating as a failed capture.");
            return 7;
        }

        Log("SUCCESS: capture completed and file is > 1 MB.");
        return 0;
    }
}
