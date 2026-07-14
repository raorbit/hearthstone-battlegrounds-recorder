using System.Diagnostics;
using System.Text.Json;
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
using Microsoft.Data.Sqlite;
using Xunit;

namespace BgRecorder.E2E.Tests;

/// <summary>
/// Skips unless a live Hearthstone process is running — the walking-skeleton E2E records the
/// real game window, so there is nothing meaningful to capture without it.
/// </summary>
public sealed class HearthstoneFactAttribute : FactAttribute
{
    public HearthstoneFactAttribute()
    {
        if (LiveHearthstone.Find() is null)
            Skip = "Hearthstone is not running; the walking-skeleton E2E records the live game window.";
    }
}

/// <summary>Resolves the live Hearthstone process (the same binding the App's locator performs).</summary>
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

/// <summary>
/// The M2 walking-skeleton exit test, fully unattended: a sanitized real-match fixture is streamed
/// through <see cref="TestFeedWriter"/> into a sandboxed fake install dir while the REAL stack runs —
/// <see cref="GameEventSource"/> discovery/tail/parse, <see cref="SessionCoordinator"/>,
/// <see cref="ScreenRecorderLibRecorder"/> capturing the live Hearthstone window,
/// <see cref="AudioCaptureEngine"/> process-loopback audio, <see cref="MediaFoundationMuxer"/>, and the
/// real SQLite repository + assembler on a temp database. The outcome must be exactly one muxed library
/// MP4 (one h264 + one aac stream) and one correct match row with markers, with staging left empty.
/// </summary>
[Trait("Category", "E2E")]
public sealed class WalkingSkeletonTests : IDisposable
{
    /// <summary>
    /// A benign teardown line (as the live game would print after the endgame placement settle):
    /// the parser defers <see cref="MatchEnded"/> past the post-COMPLETE leaderboard burst and emits
    /// it on the first non-leaderboard GameState line — the distilled fixture ends exactly at
    /// STATE=COMPLETE, so this trailer stands in for the teardown lines sanitization stripped.
    /// </summary>
    private const string TeardownTrailerLine =
        "D 23:59:00.0000000 GameState.DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value=FINAL_GAMEOVER";

    private readonly string _root;
    private readonly string _install;
    private readonly string _staging;
    private readonly string _library;
    private readonly string _dbPath;

    public WalkingSkeletonTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "bgrec-walking-skeleton-" + Guid.NewGuid().ToString("N"));
        _install = Path.Combine(_root, "install");
        _staging = Path.Combine(_root, "staging");
        _library = Path.Combine(_root, "library");
        _dbPath = Path.Combine(_root, "library.db");
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
    public async Task Streamed_fixture_produces_one_muxed_library_mp4_and_one_correct_match_row()
    {
        // Same seed shape GameEventSource derives from the session folder name.
        var seedLocal = new DateTime(2026, 7, 13, 21, 56, 22);
        var seed = new DateTimeOffset(seedLocal, TimeZoneInfo.Local.GetUtcOffset(seedLocal));

        string fixture = LocateFixture("match-03.txt");
        var expected = ExpectedFromDirectParse(fixture, seed);

        // Self-check the harness against the Spike A ground truth for match-03 before going live.
        Assert.Equal("TB_BaconShop_HERO_78", expected.Hero);
        Assert.Equal(1, expected.Place);
        Assert.Equal(13, expected.TavernTurns);
        Assert.Equal(13, expected.CombatCount);

        var settings = new AppSettings
        {
            HearthstoneInstallDir = _install,
            LibraryDir = _library,
            StagingDir = _staging,
        };

        IMatchRepository repository = new SqliteMatchRepository(_dbPath);
        await repository.InitializeAsync();

        var writer = new TestFeedWriter(_install, seed);

        var diagnostics = new List<string>();
        var gate = new object();
        int sessionDiscovered = 0;

        await using var source = new GameEventSource(
            _install,
            pollInterval: TimeSpan.FromMilliseconds(25),
            rediscoverInterval: TimeSpan.FromMilliseconds(500));
        source.EventReceived += e =>
        {
            if (e is LogSessionChanged)
                Interlocked.Exchange(ref sessionDiscovered, 1);
        };

        await using var coordinator = new SessionCoordinator(
            source,
            new ScreenRecorderLibRecorder(),
            new AudioCaptureEngine(),
            new MediaFoundationMuxer(),
            new MatchAssembler(),
            repository,
            new DiskSafety(_staging, repository),
            new LiveHearthstone.Locator(),
            settings);
        coordinator.Diagnostic += message => { lock (gate) diagnostics.Add(message); };

        await coordinator.StartAsync(CancellationToken.None);

        // The tailer opens Power.log at its end; nothing may be fed before it is watching.
        await WaitUntilAsync(
            () => Volatile.Read(ref sessionDiscovered) == 1,
            TimeSpan.FromSeconds(10), "log session discovery", diagnostics, gate);

        // Stream the fixture fast — no real-time pacing, just small yields so the tailer keeps up.
        foreach (var chunk in File.ReadLines(fixture).Chunk(50))
        {
            writer.AppendLines(chunk);
            await Task.Delay(10);
        }

        await WaitUntilAsync(
            () => coordinator.State == CoordinatorState.Recording,
            TimeSpan.FromSeconds(30), "coordinator entering Recording", diagnostics, gate);

        // Let the real capture accumulate a few seconds of live game frames and audio.
        await Task.Delay(TimeSpan.FromSeconds(3));

        // The teardown trailer releases the deferred MatchEnded -> finalize (stop, mux, insert).
        writer.AppendLine(TeardownTrailerLine);

        await WaitUntilAsync(
            () => coordinator.State == CoordinatorState.Armed,
            TimeSpan.FromSeconds(90), "finalize completing (back to Armed)", diagnostics, gate);

        // --- Library: exactly one MP4 with one h264 video and one aac audio stream. ---
        var mp4 = Assert.Single(Directory.GetFiles(_library, "*.mp4"));
        var probe = Ffprobe(mp4);
        var video = Assert.Single(probe.Streams, s => s.CodecType == "video");
        Assert.Equal("h264", video.CodecName);
        var audio = Assert.Single(probe.Streams, s => s.CodecType == "audio");
        Assert.Equal("aac", audio.CodecName);

        // --- One match row carrying the match-03 ground truth. ---
        var match = Assert.Single(await repository.ListMatchesAsync());
        Assert.Equal(expected.Hero, match.HeroCardId);
        Assert.Equal(expected.Place, match.Place);
        Assert.Equal(expected.TavernTurns, match.TavernTurns);
        Assert.Equal(BgGameType.Solo, match.GameType);
        Assert.Equal(PlayState.Won, match.PlayState);
        Assert.False(match.Truncated);
        Assert.Equal(VideoStatus.Complete, match.VideoStatus);
        Assert.Equal(mp4, match.VideoPath);
        Assert.True(match.VideoSizeBytes is > 0, "video size must be recorded");
        Assert.NotNull(match.EndedAt);

        // --- Markers: one per combat, plus turn and match-end markers per the assembler's rules. ---
        var markers = CountMarkers(match.Id);
        Assert.Equal(expected.CombatCount, markers.Combat);
        Assert.Equal(expected.TurnCount, markers.Turn);
        Assert.Equal(1, markers.End);
        Assert.Equal(expected.CombatCount + expected.TurnCount + 1, markers.Total);

        // --- Staging: the session folder was cleaned up after the row committed. ---
        Assert.Empty(Directory.GetFileSystemEntries(_staging));
    }

    // ---------------------------------------------------------------- expected values

    private sealed record ExpectedOutcome(string Hero, int Place, int TavernTurns, int CombatCount, int TurnCount);

    /// <summary>Drives the parser directly over the fixture — the reference for what the live path must persist.</summary>
    private static ExpectedOutcome ExpectedFromDirectParse(string fixturePath, DateTimeOffset seed)
    {
        var parser = new PowerLogParser(seed);
        var events = new List<GameEvent>();
        foreach (var line in File.ReadLines(fixturePath))
            events.AddRange(parser.Feed(line));
        events.AddRange(parser.Flush());

        var end = events.OfType<MatchEnded>().Single();
        return new ExpectedOutcome(
            Hero: events.OfType<LocalHeroResolved>().Last().HeroCardId,
            Place: end.FinalPlace ?? -1,
            TavernTurns: events.OfType<TurnStarted>().Max(t => t.TavernTurn),
            CombatCount: events.OfType<CombatStarted>().Count(),
            TurnCount: events.OfType<TurnStarted>().Count());
    }

    private static string LocateFixture(string name)
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

    // ---------------------------------------------------------------- db + probe helpers

    private sealed record MarkerCounts(int Combat, int Turn, int End, int Total);

    private MarkerCounts CountMarkers(long matchId)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT kind, COUNT(*) FROM markers WHERE match_id = @id GROUP BY kind;";
        cmd.Parameters.AddWithValue("@id", matchId);

        int combat = 0, turn = 0, end = 0, total = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var kind = (MarkerKind)reader.GetInt32(0);
            int count = reader.GetInt32(1);
            total += count;
            switch (kind)
            {
                case MarkerKind.CombatStart: combat = count; break;
                case MarkerKind.TurnStart: turn = count; break;
                case MarkerKind.MatchEnd: end = count; break;
            }
        }
        return new MarkerCounts(combat, turn, end, total);
    }

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
        return new ProbeResult(streams);
    }

    private sealed record ProbeStream(string CodecType, string CodecName);

    private sealed record ProbeResult(List<ProbeStream> Streams);

    private static async Task WaitUntilAsync(
        Func<bool> condition, TimeSpan timeout, string what, List<string> diagnostics, object gate)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
            {
                string diag;
                lock (gate) diag = diagnostics.Count == 0 ? "(no coordinator diagnostics)" : string.Join(" | ", diagnostics);
                throw new TimeoutException($"Timed out after {timeout} waiting for {what}. Coordinator diagnostics: {diag}");
            }
            await Task.Delay(25);
        }
    }
}
