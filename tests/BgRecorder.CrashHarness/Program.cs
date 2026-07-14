using System.Diagnostics;
using BgRecorder.Audio;
using BgRecorder.Audio.Muxing;
using BgRecorder.Capture;
using BgRecorder.Core;
using BgRecorder.Core.Capture;
using BgRecorder.Core.Data;
using BgRecorder.Core.Events;
using BgRecorder.Core.Session;
using BgRecorder.Data;
using BgRecorder.Logs;
using BgRecorder.Session;

// ---------------------------------------------------------------------------------------------
// Crash-recovery harness. Invoked by the E2E test as:  dotnet exec BgRecorder.CrashHarness.dll <sandboxDir>
//
// It wires the REAL M2 stack against the sandbox, streams sanitized fixture match-03 through the
// real discovery/tail/parse path until the coordinator is mid-recording, prints a machine-readable
// "RECORDING" line, then idles forever emitting heartbeats. It deliberately never feeds the match
// teardown trailer, so MatchEnded is never emitted and the coordinator stays in Recording — when
// the parent hard-kills this process, a fragmented MP4 + an un-finalized manifest are left behind
// exactly as a real crash would leave them.
//
// Protocol (stdout, one token per line):
//   NO_GAME        Hearthstone is not running; nothing to capture.       (exit 3)
//   NO_DISCOVERY   The tailer never latched the log folder.              (exit 4)
//   NO_RECORDING   The coordinator never reached Recording.              (exit 5)
//   RECORDING      Mid-recording; the staged fragmented MP4 is growing.  (then idles until killed)
//   TICK <bytes>   Heartbeat with the current staged video size.
//   DIAG <text>    A coordinator diagnostic message.
// ---------------------------------------------------------------------------------------------

if (args.Length < 1)
{
    Console.WriteLine("USAGE BgRecorder.CrashHarness <sandboxDir>");
    return 2;
}

string sandbox = args[0];
string install = Path.Combine(sandbox, "install");
string staging = Path.Combine(sandbox, "staging");
string library = Path.Combine(sandbox, "library");
string dbPath = Path.Combine(sandbox, "harness.db");
Directory.CreateDirectory(install);
Directory.CreateDirectory(staging);
Directory.CreateDirectory(library);

var target = LiveHearthstone.Find();
if (target is null)
{
    Console.WriteLine("NO_GAME");
    return 3;
}

var settings = new AppSettings
{
    HearthstoneInstallDir = install,
    LibraryDir = library,
    StagingDir = staging,
};

IMatchRepository repository = new SqliteMatchRepository(dbPath);
await repository.InitializeAsync();

// Create the session folder + empty Power.log BEFORE the tailer starts, so discovery has something to
// latch (the tailer opens the newest folder's log at its end and polls for appended bytes).
var seedLocal = new DateTime(2026, 7, 13, 21, 56, 22);
var seed = new DateTimeOffset(seedLocal, TimeZoneInfo.Local.GetUtcOffset(seedLocal));
var writer = new TestFeedWriter(install, seed);
string fixture = LocateFixture("match-03.txt");

int discovered = 0;
await using var source = new GameEventSource(
    install,
    pollInterval: TimeSpan.FromMilliseconds(25),
    rediscoverInterval: TimeSpan.FromMilliseconds(500));
source.EventReceived += e =>
{
    if (e is LogSessionChanged)
        Interlocked.Exchange(ref discovered, 1);
};

await using var coordinator = new SessionCoordinator(
    source,
    new ScreenRecorderLibRecorder(),
    new AudioCaptureEngine(),
    new MediaFoundationMuxer(),
    new MatchAssembler(),
    repository,
    new DiskSafety(staging, repository),
    new LiveHearthstone.Locator(),
    settings);
coordinator.Diagnostic += message => Console.WriteLine("DIAG " + message);

await coordinator.StartAsync(CancellationToken.None);

// The tailer opens Power.log at its end; nothing may be fed before it is watching.
if (!await WaitUntilAsync(() => Volatile.Read(ref discovered) == 1, TimeSpan.FromSeconds(15)))
{
    Console.WriteLine("NO_DISCOVERY");
    return 4;
}

// Stream the whole fixture (which ends at STATE=COMPLETE) but WITHOUT the teardown trailer, so the
// parser holds MatchEnded back and the coordinator stays Recording. Small yields keep the tailer fed.
foreach (var chunk in File.ReadLines(fixture).Chunk(50))
{
    writer.AppendLines(chunk);
    await Task.Delay(10);
}

if (!await WaitUntilAsync(() => coordinator.State == CoordinatorState.Recording, TimeSpan.FromSeconds(30)))
{
    Console.WriteLine("NO_RECORDING");
    return 5;
}

Console.WriteLine("RECORDING");

// Idle until the parent hard-kills us. Heartbeats carry the growing staged video size so the parent
// can confirm the fragmented MP4 is actually accumulating bytes before it pulls the trigger.
while (true)
{
    await Task.Delay(500);
    Console.WriteLine("TICK " + StagedVideoBytes(staging));
}

static long StagedVideoBytes(string staging)
{
    try
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(staging, "video.mp4", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; } catch { /* mid-write */ }
        }
        return total;
    }
    catch
    {
        return 0;
    }
}

static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
{
    var sw = Stopwatch.StartNew();
    while (!condition())
    {
        if (sw.Elapsed > timeout)
            return false;
        await Task.Delay(25);
    }
    return true;
}

static string LocateFixture(string name)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        string candidate = Path.Combine(dir.FullName, "spikes", "SpikeA.LogFidelity", "fixtures", "sanitized", name);
        if (File.Exists(candidate))
            return candidate;
        dir = dir.Parent!;
    }
    throw new FileNotFoundException("Could not locate the sanitized fixture above " + AppContext.BaseDirectory, name);
}

/// <summary>Resolves the live Hearthstone process — the same read-only binding the App's locator performs.</summary>
internal static class LiveHearthstone
{
    public const string ProcessName = "Hearthstone";

    public static RecordingTarget? Find()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        try
        {
            foreach (var p in processes)
            {
                if (!p.HasExited)
                    return new RecordingTarget(p.Id, ProcessName);
            }
            return null;
        }
        finally
        {
            foreach (var p in processes)
                p.Dispose();
        }
    }

    public sealed class Locator : IGameProcessLocator
    {
        public RecordingTarget? FindGame() => Find();
    }
}
