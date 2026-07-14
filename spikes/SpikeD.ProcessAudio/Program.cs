using System.Diagnostics;
using SpikeD.ProcessAudio;

// ---------------------------------------------------------------------------
// Spike D — game-only audio via WASAPI process loopback.
//
// Captures audio from ONLY the Hearthstone process tree
// (AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK), then also records a short
// full-system loopback clip as the Windows-10 fallback proof.
// ---------------------------------------------------------------------------

// Minimum build for AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK.
const int ProcessLoopbackMinBuild = 20348;

var options = CliOptions.Parse(args);

string tempDir = Path.GetTempPath();
string processOut = options.OutPath ?? Path.Combine(tempDir, "spikeD-hearthstone.wav");
string systemOut = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(processOut)) ?? tempDir, "spikeD-system.wav");

Console.WriteLine("=== Spike D: game-only audio via WASAPI process loopback ===");
Console.WriteLine($"Target process : {options.ProcessName}");
Console.WriteLine($"Duration       : {options.Seconds}s (process loopback), 3s (system fallback)");
Console.WriteLine($"Include tree   : {options.IncludeTree}");
Console.WriteLine($"Process-out    : {processOut}");
Console.WriteLine($"System-out     : {systemOut}");

int osBuild = Environment.OSVersion.Version.Build;
Console.WriteLine($"Windows build  : {osBuild}");
Console.WriteLine();

// --- Guard: process loopback needs build 20348+ ------------------------------
bool processLoopbackAvailable = osBuild >= ProcessLoopbackMinBuild;
if (!processLoopbackAvailable)
{
    Console.WriteLine($"[FALLBACK] Windows build {osBuild} is below {ProcessLoopbackMinBuild}.");
    Console.WriteLine("[FALLBACK] Game-only audio (process loopback) is NOT available on this OS.");
    Console.WriteLine("[FALLBACK] The app would fall back to full system-output loopback, with the");
    Console.WriteLine("[FALLBACK] audio option relabelled honestly (captures all desktop sound, not");
    Console.WriteLine("[FALLBACK] just the game). Skipping the process-loopback capture below.");
    Console.WriteLine();
}

// --- Resolve target PID ------------------------------------------------------
uint targetPid = 0;
Process? target = null;
{
    var matches = Process.GetProcessesByName(options.ProcessName);
    if (matches.Length == 0)
    {
        Console.WriteLine($"[WARN] No running process named '{options.ProcessName}'. Process-loopback capture cannot run.");
    }
    else
    {
        target = matches.OrderBy(p => { try { return p.StartTime; } catch { return DateTime.MaxValue; } }).First();
        targetPid = (uint)target.Id;
        Console.WriteLine($"[INFO] Resolved '{options.ProcessName}' -> PID {targetPid}.");
    }
}

CaptureResult? processResult = null;
string? processError = null;

if (processLoopbackAvailable && targetPid != 0)
{
    Console.WriteLine();
    Console.WriteLine($"[CAPTURE] Process loopback: PID {targetPid}, {options.Seconds}s ...");
    try
    {
        // Always capture the target (the game). WASAPI process loopback has no "target without its
        // child tree" mode, so the only correct choice for game audio is IncludeTargetProcessTree.
        processResult = ProcessLoopbackCapture.Capture(targetPid, captureEverythingExceptTarget: false, options.Seconds, processOut);
        PrintResult("Process loopback (game only)", processResult.Value);
    }
    catch (Exception ex)
    {
        processError = ex.Message;
        Console.WriteLine($"[ERROR] Process loopback failed: {ex.Message}");
    }
}

// --- System loopback (Windows-10 fallback path proof) ------------------------
CaptureResult? systemResult = null;
string? systemError = null;

Console.WriteLine();
Console.WriteLine("[CAPTURE] System loopback (fallback path): 3s ...");
try
{
    systemResult = SystemLoopbackCapture.Capture(3, systemOut);
    PrintResult("System loopback (full desktop)", systemResult.Value);
}
catch (Exception ex)
{
    systemError = ex.Message;
    Console.WriteLine($"[ERROR] System loopback failed: {ex.Message}");
}

// --- Verdict summary ---------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== Summary ===");
if (processResult is { } pr)
{
    string quality = pr.Silent
        ? "SILENT (pipeline OK — game may be muted; see README)"
        : "NON-SILENT (game audio captured)";
    Console.WriteLine($"Process loopback : OK, {quality}");
    Console.WriteLine($"                   peak {pr.PeakDb,7:0.0} dBFS, rms {pr.RmsDb,7:0.0} dBFS, {pr.DurationSeconds:0.00}s, {pr.SampleRate}Hz/{pr.BitsPerSample}bit/{pr.Channels}ch");
}
else if (!processLoopbackAvailable)
{
    Console.WriteLine("Process loopback : SKIPPED (OS below build 20348)");
}
else
{
    Console.WriteLine($"Process loopback : FAILED ({processError ?? "no target process"})");
}

if (systemResult is { } sr)
{
    Console.WriteLine($"System loopback  : OK, peak {sr.PeakDb:0.0} dBFS, rms {sr.RmsDb:0.0} dBFS, {sr.DurationSeconds:0.00}s, {sr.SampleRate}Hz/{sr.BitsPerSample}bit/{sr.Channels}ch");
}
else
{
    Console.WriteLine($"System loopback  : FAILED ({systemError})");
}

// Exit code: 0 if the primary path produced a valid WAV (silence still counts as
// a working pipeline); 2 if it hard-failed; 1 if it was skipped for OS reasons.
int exitCode = processResult is not null ? 0 : (processLoopbackAvailable ? 2 : 1);
return exitCode;

static void PrintResult(string label, CaptureResult r)
{
    Console.WriteLine($"[{label}]");
    Console.WriteLine($"    format   : {r.SampleRate} Hz, {r.BitsPerSample}-bit, {r.Channels} ch");
    Console.WriteLine($"    duration : {r.DurationSeconds:0.000} s  ({r.DataBytes:N0} data bytes)");
    Console.WriteLine($"    peak     : {r.Peak:0.000000}  ({r.PeakDb:0.0} dBFS)");
    Console.WriteLine($"    rms      : {r.Rms:0.000000}  ({r.RmsDb:0.0} dBFS)");
    Console.WriteLine($"    silent   : {r.Silent}");
}

internal sealed record CliOptions(string ProcessName, int Seconds, string? OutPath, bool IncludeTree)
{
    public static CliOptions Parse(string[] args)
    {
        string processName = "Hearthstone";
        int seconds = 10;
        string? outPath = null;
        bool includeTree = true;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--process" when i + 1 < args.Length:
                    processName = StripExe(args[++i]);
                    break;
                case "--seconds" when i + 1 < args.Length:
                    seconds = int.Parse(args[++i]);
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                case "--include-tree" when i + 1 < args.Length:
                    includeTree = ParseBool(args[++i]);
                    break;
                case "--include-tree":
                    includeTree = true;
                    break;
                case "--no-include-tree":
                    includeTree = false;
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
            }
        }

        return new CliOptions(processName, seconds, outPath, includeTree);
    }

    private static string StripExe(string name)
        => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static bool ParseBool(string value)
        => value is "1" or "true" or "yes" or "on" || value.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: SpikeD.ProcessAudio [--process <name>] [--seconds <n>] [--out <wav>] [--include-tree true|false]");
        Console.WriteLine("  --process       target process name (default: Hearthstone)");
        Console.WriteLine("  --seconds       process-loopback capture length (default: 10)");
        Console.WriteLine("  --out           output WAV path (default: %TEMP%\\spikeD-hearthstone.wav)");
        Console.WriteLine("  --include-tree  capture the process's child tree too (default: true)");
    }
}
