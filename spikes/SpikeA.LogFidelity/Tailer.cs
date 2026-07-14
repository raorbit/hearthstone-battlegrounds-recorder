using System.Diagnostics;
using System.Text;

namespace SpikeA.LogFidelity;

/// <summary>
/// Live tailer: discovers the newest log folder, opens Power.log with FileShare.ReadWrite (the game holds
/// it open for writing), seeks to end, and polls for appended bytes. New GameState events are parsed and
/// printed (match start/end, turn changes, combat starts). Every few seconds it re-runs discovery so a new
/// session folder (game restart) is picked up. Prints a bytes-watched heartbeat so an idle run (game at
/// the menu → zero events) still visibly proves the file opens and polls cleanly.
/// </summary>
public sealed class Tailer
{
    private readonly string _installDir;
    private readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(250);
    private readonly TimeSpan _rediscoverEvery = TimeSpan.FromSeconds(3);
    private readonly TimeSpan _heartbeatEvery = TimeSpan.FromSeconds(2);

    public Tailer(string installDir) => _installDir = installDir;

    public void Run(TimeSpan duration)
    {
        var overall = Stopwatch.StartNew();
        var sinceHeartbeat = Stopwatch.StartNew();
        var sinceRediscover = Stopwatch.StartNew();

        var disc = Discovery.Discover(_installDir);
        if (disc.PowerLogPath is null)
        {
            Console.WriteLine($"[tail] no Power.log found under {disc.LogsDir} — nothing to tail.");
            return;
        }

        string currentPath = disc.PowerLogPath;
        string currentFolder = disc.Chosen!.Name;
        var cursor = new DateCursor(
            DateOnly.FromDateTime(disc.SeedDate ?? DateTime.Today),
            disc.SeedTime ?? TimeSpan.Zero);
        var parser = new BgMatchParser();

        Console.WriteLine($"[tail] watching {currentFolder}\\Power.log");
        FileStream fs = OpenAtEnd(currentPath, out long startLen, out long pos);
        long baseline = startLen;
        var partial = new StringBuilder();
        int events = 0;

        try
        {
            while (overall.Elapsed < duration)
            {
                // Re-check for a newer session folder (game restart).
                if (sinceRediscover.Elapsed >= _rediscoverEvery)
                {
                    sinceRediscover.Restart();
                    var again = Discovery.Discover(_installDir);
                    if (again.PowerLogPath is not null && again.Chosen!.Name != currentFolder)
                    {
                        Console.WriteLine($"[tail] newer session detected: {again.Chosen.Name} — switching");
                        fs.Dispose();
                        currentPath = again.PowerLogPath;
                        currentFolder = again.Chosen.Name;
                        fs = OpenAtEnd(currentPath, out startLen, out pos);
                        baseline = startLen;
                        partial.Clear();
                    }
                }

                long len = fs.Length;
                if (len > pos)
                {
                    fs.Seek(pos, SeekOrigin.Begin);
                    var buf = new byte[len - pos];
                    int read = fs.Read(buf, 0, buf.Length);
                    pos += read;
                    partial.Append(Encoding.UTF8.GetString(buf, 0, read));

                    string text = partial.ToString();
                    int nl;
                    while ((nl = text.IndexOf('\n')) >= 0)
                    {
                        string line = text[..nl].TrimEnd('\r');
                        text = text[(nl + 1)..];
                        events += EmitEvents(line, cursor);
                    }
                    partial.Clear();
                    partial.Append(text);
                }
                else if (len < pos)
                {
                    // File truncated/rotated under us — reset to new end.
                    Console.WriteLine("[tail] file shrank (rotation?) — reseeking to end");
                    pos = len;
                    baseline = len;
                    partial.Clear();
                }

                if (sinceHeartbeat.Elapsed >= _heartbeatEvery)
                {
                    sinceHeartbeat.Restart();
                    Console.WriteLine(
                        $"[tail] +{overall.Elapsed.TotalSeconds,4:F0}s  watched={pos - baseline,10:N0} bytes  events={events}");
                }

                Thread.Sleep(_pollInterval);
            }
        }
        finally
        {
            fs.Dispose();
        }

        Console.WriteLine($"[tail] done after {overall.Elapsed.TotalSeconds:F0}s. total new bytes={pos - baseline:N0}, events={events}");
    }

    private static FileStream OpenAtEnd(string path, out long startLen, out long pos)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        startLen = fs.Length;
        pos = startLen;
        return fs;
    }

    /// <summary>Prints human-readable events for one line; returns how many were emitted.</summary>
    private static int EmitEvents(string raw, DateCursor cursor)
    {
        if (!LogLine.TryParse(raw, out var ln) || !ln.IsGameState) return 0;
        var when = cursor.Advance(ln.TimeOfDay);
        string p = ln.Payload;
        string ts = when.ToString("HH:mm:ss");

        if (ln.IsGameStatePower && p.StartsWith("CREATE_GAME", StringComparison.Ordinal))
        { Console.WriteLine($"[tail] {ts}  >>> MATCH START"); return 1; }

        if (p.StartsWith("TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE", StringComparison.Ordinal))
        { Console.WriteLine($"[tail] {ts}  <<< MATCH COMPLETE"); return 1; }

        if (p.StartsWith("TAG_CHANGE Entity=GameEntity tag=TURN value=", StringComparison.Ordinal))
        {
            int v = int.Parse(p["TAG_CHANGE Entity=GameEntity tag=TURN value=".Length..].TrimEnd());
            string phase = v % 2 == 0 ? "combat" : "recruit";
            Console.WriteLine($"[tail] {ts}  turn raw={v} (tavern {(v + 1) / 2}, {phase})");
            return 1;
        }

        if (p.StartsWith("TAG_CHANGE Entity=GameEntity tag=BOARD_VISUAL_STATE value=2", StringComparison.Ordinal))
        { Console.WriteLine($"[tail] {ts}  --- combat start (board view)"); return 1; }

        return 0;
    }
}
