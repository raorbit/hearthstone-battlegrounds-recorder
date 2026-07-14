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

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<GameEvent>? EventReceived;

    public GameEventSource(string installDir, TimeSpan? pollInterval = null, TimeSpan? rediscoverInterval = null)
    {
        _installDir = installDir;
        _poll = pollInterval ?? DefaultPoll;
        _rediscover = rediscoverInterval ?? DefaultRediscover;
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
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    private void Emit(GameEvent e) => EventReceived?.Invoke(e);

    private void RunLoop(CancellationToken ct)
    {
        // Wait for a Power.log to exist (game may launch after us).
        var disc = LogSessionDiscovery.Discover(_installDir);
        while (!ct.IsCancellationRequested && disc.PowerLogPath is null)
        {
            if (ct.WaitHandle.WaitOne(_rediscover)) return;
            disc = LogSessionDiscovery.Discover(_installDir);
        }
        if (ct.IsCancellationRequested) return;

        string currentFolder = disc.Chosen!.Name;
        Emit(new LogSessionChanged(SeedStamp(disc), disc.Chosen.Path));

        var parser = new PowerLogParser(SeedStamp(disc));
        var decoder = Encoding.UTF8.GetDecoder();
        var partial = new StringBuilder();
        FileStream fs = OpenAtEnd(disc.PowerLogPath!, out long pos);
        long sinceRediscover = Environment.TickCount64;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (Environment.TickCount64 - sinceRediscover >= (long)_rediscover.TotalMilliseconds)
                {
                    sinceRediscover = Environment.TickCount64;
                    var again = LogSessionDiscovery.Discover(_installDir);
                    if (again.PowerLogPath is not null && again.Chosen!.Name != currentFolder)
                    {
                        // Drain the outgoing file, then close its match as truncated if still open.
                        DrainAndParse(fs, decoder, partial, ref pos, parser);
                        foreach (var e in parser.Flush()) Emit(e);
                        fs.Dispose();

                        currentFolder = again.Chosen.Name;
                        Emit(new LogSessionChanged(SeedStamp(again), again.Chosen.Path));
                        parser = new PowerLogParser(SeedStamp(again));
                        decoder = Encoding.UTF8.GetDecoder();
                        partial.Clear();
                        fs = OpenAtEnd(again.PowerLogPath!, out pos);
                        continue;
                    }
                }

                DrainAndParse(fs, decoder, partial, ref pos, parser);
                if (ct.WaitHandle.WaitOne(_poll)) break;
            }

            // Final drain + flush so an in-progress match is reported (truncated) on stop.
            DrainAndParse(fs, decoder, partial, ref pos, parser);
            foreach (var e in parser.Flush()) Emit(e);
        }
        finally
        {
            fs.Dispose();
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
                foreach (var e in parser.Feed(line)) Emit(e);
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

    private static FileStream OpenAtEnd(string path, out long pos)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
