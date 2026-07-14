using BgRecorder.Core.Data;
using BgRecorder.Core.Events;
using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BgRecorder.Data.Tests;

public sealed class SqliteMatchRepositoryTests
{
    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"bgrec-test-{Guid.NewGuid():N}.db");

    private static async Task<SqliteMatchRepository> NewRepoAsync(string dbPath)
    {
        var repo = new SqliteMatchRepository(dbPath);
        await repo.InitializeAsync();
        return repo;
    }

    private static SqliteConnection OpenRaw(string dbPath)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            ForeignKeys = true,
        }.ToString());
        conn.Open();
        return conn;
    }

    private static void Cleanup(string dbPath)
    {
        // Pooled connections keep a handle on the file; release them before deleting.
        SqliteConnection.ClearAllPools();
        foreach (var p in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort in tests */ }
        }
    }

    private static MatchRecord SampleMatch() => new()
    {
        // Non-round offset + sub-millisecond ticks to exercise date/offset fidelity.
        StartedAt = new DateTimeOffset(2026, 7, 14, 9, 15, 30, 123, TimeSpan.FromHours(-7)).AddTicks(4567),
        EndedAt = new DateTimeOffset(2026, 7, 14, 9, 42, 10, 500, TimeSpan.FromHours(-7)),
        GameType = BgGameType.Solo,
        HeroCardId = "BG_HERO_100",
        Place = 2,
        TavernTurns = 11,
        PlayState = PlayState.Won,
        Truncated = false,
        VideoStatus = VideoStatus.Complete,
        VideoPath = @"C:\vods\match.mp4",
        VideoSizeBytes = 1_234_567_890L,
        VideoDuration = TimeSpan.FromMilliseconds(1_805_000),
        Starred = true,
        ManualRating = 8123,
    };

    private static IReadOnlyList<MarkerRecord> SampleMarkers() =>
    [
        new MarkerRecord(0, MarkerKind.TurnStart, 0, 1),
        new MarkerRecord(0, MarkerKind.CombatStart, 45_000, 1),
        new MarkerRecord(0, MarkerKind.MatchEnd, 1_600_000, 11),
    ];

    // All SQLite INTEGER columns come back as Int64; Dapper's record binding requires the
    // constructor parameter types to match, so every field is long here.
    private sealed record MarkerReadback(long MatchId, long Kind, long AtMs, long TavernTurn);

    private const string MarkerSelect =
        "SELECT match_id AS MatchId, kind AS Kind, at_ms AS AtMs, tavern_turn AS TavernTurn " +
        "FROM markers WHERE match_id = @id ORDER BY at_ms;";

    [Fact]
    public async Task Insert_then_list_round_trips_all_fields()
    {
        var db = NewDbPath();
        try
        {
            var repo = await NewRepoAsync(db);
            var match = SampleMatch();

            var id = await repo.InsertMatchAsync(match, SampleMarkers());
            Assert.True(id > 0);

            var row = Assert.Single(await repo.ListMatchesAsync());

            Assert.Equal(id, row.Id);
            Assert.Equal(match.StartedAt, row.StartedAt);
            Assert.Equal(match.StartedAt.Offset, row.StartedAt.Offset);
            Assert.Equal(match.EndedAt, row.EndedAt);
            Assert.Equal(match.EndedAt!.Value.Offset, row.EndedAt!.Value.Offset);
            Assert.Equal(match.GameType, row.GameType);
            Assert.Equal(match.HeroCardId, row.HeroCardId);
            Assert.Equal(match.Place, row.Place);
            Assert.Equal(match.TavernTurns, row.TavernTurns);
            Assert.Equal(match.PlayState, row.PlayState);
            Assert.Equal(match.Truncated, row.Truncated);
            Assert.Equal(match.VideoStatus, row.VideoStatus);
            Assert.Equal(match.VideoPath, row.VideoPath);
            Assert.Equal(match.VideoSizeBytes, row.VideoSizeBytes);
            Assert.Equal(match.VideoDuration, row.VideoDuration);
            Assert.Equal(match.Starred, row.Starred);
            Assert.Equal(match.ManualRating, row.ManualRating);

            await using var raw = OpenRaw(db);
            var markers = (await raw.QueryAsync<MarkerReadback>(MarkerSelect, new { id })).ToList();
            Assert.Equal(3, markers.Count);
            Assert.All(markers, m => Assert.Equal(id, m.MatchId));
            Assert.Equal((int)MarkerKind.TurnStart, (int)markers[0].Kind);
            Assert.Equal(0L, markers[0].AtMs);
            Assert.Equal(1, (int)markers[0].TavernTurn);
            Assert.Equal((int)MarkerKind.CombatStart, (int)markers[1].Kind);
            Assert.Equal(45_000L, markers[1].AtMs);
            Assert.Equal((int)MarkerKind.MatchEnd, (int)markers[2].Kind);
            Assert.Equal(1_600_000L, markers[2].AtMs);
            Assert.Equal(11, (int)markers[2].TavernTurn);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task Nullable_fields_round_trip_as_null()
    {
        var db = NewDbPath();
        try
        {
            var repo = await NewRepoAsync(db);
            var match = new MatchRecord
            {
                StartedAt = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero),
                EndedAt = null,
                GameType = BgGameType.Duos,
                HeroCardId = null,
                Place = null,
                TavernTurns = 0,
                PlayState = PlayState.Unknown,
                Truncated = true,
                VideoStatus = VideoStatus.Incomplete,
                VideoPath = null,
                VideoSizeBytes = null,
                VideoDuration = null,
                Starred = false,
                ManualRating = null,
            };

            await repo.InsertMatchAsync(match, []);
            var row = Assert.Single(await repo.ListMatchesAsync());

            Assert.Null(row.EndedAt);
            Assert.Null(row.HeroCardId);
            Assert.Null(row.Place);
            Assert.Null(row.VideoPath);
            Assert.Null(row.VideoSizeBytes);
            Assert.Null(row.VideoDuration);
            Assert.Null(row.ManualRating);
            Assert.False(row.Starred);
            Assert.True(row.Truncated);
            Assert.Equal(VideoStatus.Incomplete, row.VideoStatus);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task UpdateVideoStatus_changes_only_the_status()
    {
        var db = NewDbPath();
        try
        {
            var repo = await NewRepoAsync(db);
            var id = await repo.InsertMatchAsync(SampleMatch() with { VideoStatus = VideoStatus.Complete }, []);

            await repo.UpdateVideoStatusAsync(id, VideoStatus.Missing);

            var row = Assert.Single(await repo.ListMatchesAsync());
            Assert.Equal(VideoStatus.Missing, row.VideoStatus);
            Assert.Equal("BG_HERO_100", row.HeroCardId); // untouched
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task Deleting_a_match_cascades_to_its_markers()
    {
        var db = NewDbPath();
        try
        {
            var repo = await NewRepoAsync(db);
            var id = await repo.InsertMatchAsync(SampleMatch(), SampleMarkers());

            await using var raw = OpenRaw(db);
            Assert.Equal(3L, await raw.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM markers WHERE match_id = @id;", new { id }));

            await raw.ExecuteAsync("DELETE FROM matches WHERE id = @id;", new { id });

            Assert.Equal(0L, await raw.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM markers WHERE match_id = @id;", new { id }));
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task ListMatches_orders_newest_started_first()
    {
        var db = NewDbPath();
        try
        {
            var repo = await NewRepoAsync(db);
            var older = SampleMatch() with
            {
                StartedAt = new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.Zero),
                HeroCardId = "OLD",
            };
            var newer = SampleMatch() with
            {
                StartedAt = new DateTimeOffset(2026, 7, 14, 20, 0, 0, TimeSpan.Zero),
                HeroCardId = "NEW",
            };

            // Insert oldest first to prove ordering is by started_at, not insertion order.
            await repo.InsertMatchAsync(older, []);
            await repo.InsertMatchAsync(newer, []);

            var list = await repo.ListMatchesAsync();
            Assert.Equal(2, list.Count);
            Assert.Equal("NEW", list[0].HeroCardId);
            Assert.Equal("OLD", list[1].HeroCardId);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task Sequential_inserts_get_distinct_increasing_ids()
    {
        var db = NewDbPath();
        try
        {
            var repo = await NewRepoAsync(db);
            var ids = new List<long>();
            for (var i = 0; i < 5; i++)
                ids.Add(await repo.InsertMatchAsync(SampleMatch(), SampleMarkers()));

            Assert.Equal(5, ids.Distinct().Count());
            Assert.Equal(ids.OrderBy(x => x).ToList(), ids); // autoincrement => strictly increasing
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task Schema_version_row_is_one()
    {
        var db = NewDbPath();
        try
        {
            await NewRepoAsync(db);

            await using var raw = OpenRaw(db);
            var rows = (await raw.QueryAsync<long>("SELECT version FROM schema_version;")).ToList();
            Assert.Equal(new long[] { 1 }, rows);
            Assert.Equal(SqliteMatchRepository.SchemaVersion, (int)rows[0]);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task Initialize_is_idempotent_and_preserves_data()
    {
        var db = NewDbPath();
        try
        {
            var repo = new SqliteMatchRepository(db);
            await repo.InitializeAsync();
            var id = await repo.InsertMatchAsync(SampleMatch(), SampleMarkers());

            await repo.InitializeAsync();
            await repo.InitializeAsync();

            await using var raw = OpenRaw(db);
            Assert.Equal(1L, await raw.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM schema_version;"));
            Assert.Equal(1L, await raw.ExecuteScalarAsync<long>("SELECT version FROM schema_version;"));

            var row = Assert.Single(await repo.ListMatchesAsync());
            Assert.Equal(id, row.Id);
            Assert.Equal(3L, await raw.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM markers WHERE match_id = @id;", new { id }));
        }
        finally { Cleanup(db); }
    }
}
