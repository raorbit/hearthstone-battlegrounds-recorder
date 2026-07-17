using System.Text;
using BgRecorder.Core.Events;

namespace BgRecorder.Logs;

/// <summary>
/// Production <see cref="IGameEventSource"/>: newest-session-folder discovery + a polling
/// FileShare.ReadWrite tailer + <see cref="PowerLogParser"/>. Emits <see cref="GameEvent"/>s in log order.
///
/// The watch loop opens the newest folder's Power.log, seeks to end, and polls (~250 ms) for appended bytes.
/// Every few seconds it re-checks discovery: if a newer session folder appears (a game restart), it drains
/// the old file, flushes the parser — emitting a truncated <see cref="MatchEnded"/> if a match was open —
/// raises <see cref="LogSessionChanged"/>, and starts a fresh parser seeded from the new folder's name. On
/// stop it drains and flushes once more so an interrupted match is reported as truncated.
/// </summary>
public sealed class GameEventSource : IGameEventSource
{
    private static readonly TimeSpan DefaultPoll = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultRediscover = TimeSpan.FromSeconds(3);

    private readonly string _installDir;
    private readonly TimeSpan _poll;
    private readonly TimeSpan _rediscover;
    private readonly Func<string, FileStream> _open;
    private readonly LogHealthMonitor _health;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<GameEvent>? EventReceived;

    /// <summary>
    /// Raised (best-effort) when the watch loop catches a non-cancellation error — a transient IO/parse failure
    /// it recovers from by continuing to poll. Purely diagnostic: the loop keeps running whether or not anyone
    /// subscribes, so a blip never silently ends recording. Not part of <see cref="IGameEventSource"/>.
    /// </summary>
    public event Action<string>? Diagnostic;

    /// <summary>
    /// Raised at most once per broken stretch when <see cref="LogHealthMonitor"/> concludes the log
    /// format no longer parses (game traffic flowing, zero events). Distinct from <see cref="Diagnostic"/>
    /// so the shell can surface it loudly — this is the "silently missing every match" failure.
    /// </summary>
    public event Action<string>? HealthAlert;

    public GameEventSource(string installDir, TimeSpan? pollInterval = null, TimeSpan? rediscoverInterval = null)
        : this(installDir, pollInterval, rediscoverInterval, DefaultOpen)
    {
    }

    /// <summary>Test seam: overrides how <c>Power.log</c> is opened so a transient open failure can be simulated,
    /// and the health monitor so its thresholds can be shrunk.</summary>
    internal GameEventSource(
        string installDir,
        TimeSpan? pollInterval,
        TimeSpan? rediscoverInterval,
        Func<string, FileStream> open,
        LogHealthMonitor? health = null)
    {
        _installDir = installDir;
        _poll = pollInterval ?? DefaultPoll;
        _rediscover = rediscoverInterval ?? DefaultRediscover;
        _open = open;
        _health = health ?? new LogHealthMonitor();
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (_loop is not null) throw new InvalidOperationException("Already started.");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _loop = Task.Factory.StartNew(() => RunLoop(token), token,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        if (_loop is not null)
        {
            // Swallow cancellation and any residual fault from the loop task — the loop is built to recover
            // from transient errors, but disposal must never rethrow one it happened to surface.
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Diagnostic?.Invoke($"watch loop faulted on shutdown: {ex.GetType().Name}: {ex.Message}"); }
        }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    private void Emit(GameEvent e) => EventReceived?.Invoke(e);

    private void RunLoop(CancellationToken ct)
    {
        // Session state. `parser`/`currentFolder` identify the session in progress; `fs` is the live tail and
        // may be momentarily null (before the first log appears, or after a transient error drops it — in
        // which case the parser and its in-flight match state are preserved and the stream is simply reopened).
        string? currentFolder = null;
        string? currentPath = null;
        PowerLogParser? parser = null;
        var decoder = Encoding.UTF8.GetDecoder();
        var partial = new StringBuilder();
        FileStream? fs = null;
        long pos = 0;
        long sinceRediscover = Environment.TickCount64;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (fs is null)
                    {
                        var disc = LogSessionDiscovery.Discover(_installDir);
                        if (disc.PowerLogPath is null)
                        {
                            // No live Power.log yet (game not launched, or all folders rotated away).
                            if (ct.WaitHandle.WaitOne(_rediscover)) break;
                            continue;
                        }

                        if (parser is null || disc.Chosen!.Name != currentFolder)
                        {
                            // New or changed session. Open the new log and establish the tail position BEFORE
                            // mutating any session state, so a transient open failure leaves the prior state
                            // (or the initial null state) intact: the retry re-enters this branch and re-seeks
                            // to end, instead of falling through to the plain reopen below and either replaying
                            // the whole pre-existing log (first discovery) or resuming the new file at a stale
                            // offset — in both cases with no LogSessionChanged emitted.
                            var newParser = new PowerLogParser(SeedStamp(disc));
                            var newFs = OpenAtEnd(disc.PowerLogPath, out var newPos);

                            // Open succeeded: close out any interrupted match (truncated), then commit the
                            // new session. Reached both on first discovery and when a reopen finds a newer folder.
                            if (parser is not null)
                                foreach (var e in parser.Flush()) Emit(e);

                            currentFolder = disc.Chosen!.Name;
                            currentPath = disc.PowerLogPath;
                            parser = newParser;
                            decoder = Encoding.UTF8.GetDecoder();
                            partial.Clear();
                            fs = newFs;
                            pos = newPos;
                            Emit(new LogSessionChanged(SeedStamp(disc), disc.Chosen.Path));
                        }
                        else
                        {
                            // Same session, stream lost to a transient error: reopen and resume from `pos`
                            // (DrainAndParse re-seeks on shrink) without re-announcing or resetting the parser.
                            fs = _open(currentPath!);
                        }

                        sinceRediscover = Environment.TickCount64;
                        if (ct.WaitHandle.WaitOne(_poll)) break;
                        continue;
                    }

                    if (Environment.TickCount64 - sinceRediscover >= (long)_rediscover.TotalMilliseconds)
                    {
                        sinceRediscover = Environment.TickCount64;
                        var again = LogSessionDiscovery.Discover(_installDir);
                        if (again.PowerLogPath is not null && again.Chosen!.Name != currentFolder)
                        {
                            // Open the new session's log before tearing down the old one, so a transient open
                            // failure leaves the current session intact (the outer catch drops the old fs; the
                            // next iteration re-discovers and re-enters the new-session branch above) rather
                            // than resuming the new file at the old offset with no LogSessionChanged.
                            var newParser = new PowerLogParser(SeedStamp(again));
                            var newFs = OpenAtEnd(again.PowerLogPath, out var newPos);

                            // Drain the outgoing file, then close its match as truncated if still open.
                            DrainAndParse(fs, decoder, partial, ref pos, parser!);
                            foreach (var e in parser!.Flush()) Emit(e);
                            fs.Dispose();

                            currentFolder = again.Chosen.Name;
                            currentPath = again.PowerLogPath;
                            parser = newParser;
                            decoder = Encoding.UTF8.GetDecoder();
                            partial.Clear();
                            fs = newFs;
                            pos = newPos;
                            Emit(new LogSessionChanged(SeedStamp(again), again.Chosen.Path));
                            if (ct.WaitHandle.WaitOne(_poll)) break;
                            continue;
                        }
                    }

                    DrainAndParse(fs, decoder, partial, ref pos, parser!);
                    if (ct.WaitHandle.WaitOne(_poll)) break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Transient IO/parse failure (Discover, open, fs.Length/Read, …). Report it and keep
                    // polling — dropping the possibly-broken FileStream so the next iteration reopens cleanly.
                    // The parser and its in-progress match state survive, so a blip never ends recording.
                    Diagnostic?.Invoke($"watch loop error: {ex.GetType().Name}: {ex.Message}");
                    try { fs?.Dispose(); } catch { /* already broken */ }
                    fs = null;
                    if (ct.WaitHandle.WaitOne(_poll)) break;
                }
            }

            // Final drain + flush so an in-progress match is reported (truncated) on stop.
            if (parser is not null)
            {
                try
                {
                    if (fs is not null) DrainAndParse(fs, decoder, partial, ref pos, parser);
                    foreach (var e in parser.Flush()) Emit(e);
                }
                catch (Exception ex)
                {
                    Diagnostic?.Invoke($"final drain error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            fs?.Dispose();
        }
    }

    /// <summary>Reads any bytes appended since <paramref name="pos"/> and feeds complete lines to the parser.</summary>
    private void DrainAndParse(FileStream fs, Decoder decoder, StringBuilder partial, ref long pos, PowerLogParser parser)
    {
        long len = fs.Length;
        if (len > pos)
        {
            fs.Seek(pos, SeekOrigin.Begin);
            var buf = new byte[len - pos];
            int read = fs.Read(buf, 0, buf.Length);
            pos += read;

            // Incremental UTF-8 decode: retains any partial multi-byte char split across reads.
            var chars = new char[decoder.GetCharCount(buf, 0, read)];
            int cc = decoder.GetChars(buf, 0, read, chars, 0);
            partial.Append(chars, 0, cc);

            string text = partial.ToString();
            int nl;
            while ((nl = text.IndexOf('\n')) >= 0)
            {
                string line = text[..nl].TrimEnd('\r');
                text = text[(nl + 1)..];
                int emitted = 0;
                foreach (var e in parser.Feed(line))
                {
                    Emit(e);
                    emitted++;
                }

                if (emitted > 0)
                {
                    _health.OnEvents(emitted);
                }
                else if (_health.OnSilentLine(line, DateTimeOffset.Now) is { } alert)
                {
                    HealthAlert?.Invoke(alert);
                }
            }
            partial.Clear();
            partial.Append(text);
        }
        else if (len < pos)
        {
            // File shrank (rotation) — reseek to the new end.
            pos = len;
            partial.Clear();
        }
    }

    /// <summary>
    /// Opens the log read-only, sharing read+write+delete so a passive tailer never blocks the game appending
    /// to — or rotating away — its own Power.log (FileShare.Delete lets the game rename/replace it under us).
    /// </summary>
    private static FileStream DefaultOpen(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private FileStream OpenAtEnd(string path, out long pos)
    {
        var fs = _open(path);
        pos = fs.Length;
        return fs;
    }

    /// <summary>Seed timestamp for the parser/session event: parsed folder name if available, else its ctime.</summary>
    private static DateTimeOffset SeedStamp(LogSessionDiscovery.Result disc)
    {
        DateTime local = disc.SeedDate is { } d && disc.SeedTime is { } t
            ? d + t
            : disc.Chosen?.Stamp ?? DateTime.Now;
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
