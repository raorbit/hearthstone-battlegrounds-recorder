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
    public async Task GetMatch_returns_match_and_markers_ordered_by_offset_then_insertion()
    {
        var db = NewDbPath();
        try
        {
            var repo = await NewRepoAsync(db);
            var match = SampleMatch();
            var insertedMarkers = new MarkerRecord[]
            {
                new(0, MarkerKind.CombatStart, 45_000, 1),
                new(0, MarkerKind.MatchEnd, 90_000, 3),
                new(0, MarkerKind.TurnStart, 0, 1),
                new(0, MarkerKind.CombatStart, 45_000, 2),
            };

            var id = await repo.InsertMatchAsync(match, insertedMarkers);

            var detail = Assert.IsType<MatchDetailRecord>(await repo.GetMatchAsync(id));
            Assert.Equal(match with { Id = id }, detail.Match);
            Assert.Equal(
                new MarkerRecord[]
                {
                    new(id, MarkerKind.TurnStart, 0, 1),
                    new(id, MarkerKind.CombatStart, 45_000, 1),
                    new(id, MarkerKind.CombatStart, 45_000, 2),
                    new(id, MarkerKind.MatchEnd, 90_000, 3),
                },
                detail.Markers);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task GetMatch_returns_null_when_match_does_not_exist()
    {
        var db = NewDbPath();
        try
        {
            var repo = await NewRepoAsync(db);
            await repo.InsertMatchAsync(SampleMatch(), SampleMarkers());

            Assert.Null(await repo.GetMatchAsync(long.MaxValue));
        }
        finally { Cleanup(db); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateStarred_changes_only_target_flag_and_preserves_both_rows_markers(bool starred)
    {
        var db = NewDbPath();
        try
        {
            var repo = await NewRepoAsync(db);
            var targetId = await repo.InsertMatchAsync(
                SampleMatch() with { HeroCardId = "TARGET", Starred = !starred },
                SampleMarkers());
            var otherId = await repo.InsertMatchAsync(
                SampleMatch() with
                {
                    HeroCardId = "OTHER",
                    StartedAt = SampleMatch().StartedAt.AddMinutes(1),
                    Starred = starred,
                },
                [new MarkerRecord(0, MarkerKind.CombatStart, 12_345, 2)]);

            var targetBefore = Assert.IsType<MatchDetailRecord>(await repo.GetMatchAsync(targetId));
            var otherBefore = Assert.IsType<MatchDetailRecord>(await repo.GetMatchAsync(otherId));

            await repo.UpdateStarredAsync(targetId, starred);

            var targetAfter = Assert.IsType<MatchDetailRecord>(await repo.GetMatchAsync(targetId));
            var otherAfter = Assert.IsType<MatchDetailRecord>(await repo.GetMatchAsync(otherId));
            Assert.Equal(targetBefore.Match with { Starred = starred }, targetAfter.Match);
            Assert.Equal(targetBefore.Markers, targetAfter.Markers);
            Assert.Equal(otherBefore.Match, otherAfter.Match);
            Assert.Equal(otherBefore.Markers, otherAfter.Markers);
        }
        finally { Cleanup(db); }
    }

    [Theory]
    [InlineData(4200)]
    [InlineData(0)]
    public async Task UpdateManualRating_sets_only_the_target_row(int rating)
    {
        var db = NewDbPath();
        try
        {
            var repo = await NewRepoAsync(db);
            var targetId = await repo.InsertMatchAsync(
                SampleMatch() with { HeroCardId = "TARGET", ManualRating = null },
                SampleMarkers());
            var otherId = await repo.InsertMatchAsync(
                SampleMatch() with
                {
                    HeroCardId = "OTHER",
                    StartedAt = SampleMatch().StartedAt.AddMinutes(1),
                    ManualRating = 999,
                },
                SampleMarkers());
            var otherBefore = Assert.IsType<MatchDetailRecord>(await repo.GetMatchAsync(otherId));

            await repo.UpdateManualRatingAsync(targetId, rating);

            var targetAfter = Assert.IsType<MatchDetailRecord>(await repo.GetMatchAsync(targetId));
            var otherAfter = Assert.IsType<MatchDetailRecord>(await repo.GetMatchAsync(otherId));
            Assert.Equal(rating, targetAfter.Match.ManualRating);
            Assert.Equal(otherBefore.Match, otherAfter.Match);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task UpdateManualRating_to_null_clears_an_existing_value()
    {
        var db = NewDbPath();
        try
        {
            var repo = await NewRepoAsync(db);
            var id = await repo.InsertMatchAsync(SampleMatch() with { ManualRating = 6000 }, SampleMarkers());
            Assert.Equal(6000, Assert.IsType<MatchDetailRecord>(await repo.GetMatchAsync(id)).Match.ManualRating);

            await repo.UpdateManualRatingAsync(id, null);

            Assert.Null(Assert.IsType<MatchDetailRecord>(await repo.GetMatchAsync(id)).Match.ManualRating);
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
    public async Task Insert_is_idempotent_on_session_id()
    {
        var db = NewDbPath();
        try
        {
            var repo = await NewRepoAsync(db);
            var match = SampleMatch() with { SessionId = "abc123" };

            var id1 = await repo.InsertMatchAsync(match, SampleMarkers());
            // A crash-recovery re-run of the same staging session inserts again…
            var id2 = await repo.InsertMatchAsync(match with { VideoStatus = VideoStatus.Incomplete }, SampleMarkers());

            Assert.Equal(id1, id2);                       // …but returns the existing row, no duplicate
            var row = Assert.Single(await repo.ListMatchesAsync());
            Assert.Equal("abc123", row.SessionId);
            Assert.Equal(VideoStatus.Complete, row.VideoStatus); // original row untouched

            await using var raw = OpenRaw(db);
            Assert.Equal(1L, await raw.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM matches;"));
            // Markers were not duplicated either.
            Assert.Equal(3L, await raw.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM markers WHERE match_id = @id;", new { id = id1 }));
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task MatchExistsBySession_reflects_inserted_rows()
    {
        var db = NewDbPath();
        try
        {
            var repo = await NewRepoAsync(db);
            Assert.False(await repo.MatchExistsBySessionAsync("sess-1"));
            await repo.InsertMatchAsync(SampleMatch() with { SessionId = "sess-1" }, []);
            Assert.True(await repo.MatchExistsBySessionAsync("sess-1"));
            Assert.False(await repo.MatchExistsBySessionAsync("sess-2"));
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task Null_session_ids_do_not_collide()
    {
        var db = NewDbPath();
        try
        {
            var repo = await NewRepoAsync(db);
            // Rows without a session id (manual imports/tests) must not be deduped against each other.
            await repo.InsertMatchAsync(SampleMatch(), []);
            await repo.InsertMatchAsync(SampleMatch(), []);
            Assert.Equal(2, (await repo.ListMatchesAsync()).Count);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task ListMatches_orders_by_instant_across_mixed_offsets()
    {
        var db = NewDbPath();
        try
        {
            var repo = await NewRepoAsync(db);
            // Around a fall-back DST overlap the local wall-clock text misleads: the later row has the
            // smaller local time. Sorting must follow the true UTC instant, not the offset-bearing text.
            var newer = SampleMatch() with
            {
                StartedAt = new DateTimeOffset(2026, 11, 1, 1, 10, 0, TimeSpan.FromHours(-5)), // 06:10Z
                HeroCardId = "NEW",
            };
            var older = SampleMatch() with
            {
                StartedAt = new DateTimeOffset(2026, 11, 1, 1, 50, 0, TimeSpan.FromHours(-4)), // 05:50Z
                HeroCardId = "OLD",
            };

            await repo.InsertMatchAsync(newer, []);
            await repo.InsertMatchAsync(older, []);

            var list = await repo.ListMatchesAsync();
            Assert.Equal("NEW", list[0].HeroCardId); // 06:10Z is the newer instant despite the larger local time on OLD
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

    /// <summary>
    /// A database created by an earlier build has a <c>matches</c> table without <c>session_id</c> or
    /// <c>started_at_utc</c>. InitializeAsync must migrate it (add the columns, backfill the UTC sort
    /// key, add the uniqueness index) so insert/list/existence queries no longer fail with
    /// "no such column"; the pre-existing row must survive.
    /// </summary>
    [Fact]
    public async Task Initialize_migrates_a_legacy_matches_table_missing_the_new_columns()
    {
        var db = NewDbPath();
        try
        {
            await using (var raw = OpenRaw(db))
            {
                await raw.ExecuteAsync("""
                    CREATE TABLE schema_version (version INTEGER NOT NULL);
                    INSERT INTO schema_version (version) VALUES (1);
                    CREATE TABLE matches (
                        id                INTEGER PRIMARY KEY AUTOINCREMENT,
                        started_at        TEXT    NOT NULL,
                        ended_at          TEXT    NULL,
                        game_type         INTEGER NOT NULL,
                        hero_card_id      TEXT    NULL,
                        place             INTEGER NULL,
                        tavern_turns      INTEGER NOT NULL,
                        play_state        INTEGER NOT NULL,
                        truncated         INTEGER NOT NULL,
                        video_status      INTEGER NOT NULL,
                        video_path        TEXT    NULL,
                        video_size_bytes  INTEGER NULL,
                        video_duration_ms INTEGER NULL,
                        starred           INTEGER NOT NULL DEFAULT 0,
                        manual_rating     INTEGER NULL,
                        created_at        TEXT    NOT NULL
                    );
                    CREATE TABLE markers (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, match_id INTEGER NOT NULL,
                        kind INTEGER NOT NULL, at_ms INTEGER NOT NULL, tavern_turn INTEGER NOT NULL,
                        FOREIGN KEY (match_id) REFERENCES matches(id) ON DELETE CASCADE
                    );
                    INSERT INTO matches (started_at, game_type, tavern_turns, play_state, truncated, video_status, created_at)
                    VALUES ('2026-07-14T09:15:30.0000000-07:00', 0, 11, 2, 0, 0, '2026-07-14T16:15:30.0000000+00:00');
                    """);
            }

            var repo = await NewRepoAsync(db); // runs the migration

            // The pre-existing row survives and lists (ORDER BY started_at_utc no longer throws).
            Assert.Single(await repo.ListMatchesAsync());

            // The added columns are now usable end to end.
            await repo.InsertMatchAsync(SampleMatch() with { SessionId = "sess-123" }, SampleMarkers());
            Assert.True(await repo.MatchExistsBySessionAsync("sess-123"));

            // The legacy row got a backfilled UTC sort key.
            await using var check = OpenRaw(db);
            Assert.Equal(0L, await check.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM matches WHERE started_at_utc IS NULL;"));
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task Initialize_resumes_when_session_id_exists_without_unique_index()
    {
        var db = NewDbPath();
        try
        {
            await using (var raw = OpenRaw(db))
            {
                await raw.ExecuteAsync("""
                    CREATE TABLE schema_version (version INTEGER NOT NULL);
                    INSERT INTO schema_version (version) VALUES (1);
                    CREATE TABLE matches (
                        id                INTEGER PRIMARY KEY AUTOINCREMENT,
                        session_id        TEXT    NULL,
                        started_at        TEXT    NOT NULL,
                        started_at_utc    TEXT    NULL,
                        ended_at          TEXT    NULL,
                        game_type         INTEGER NOT NULL,
                        hero_card_id      TEXT    NULL,
                        place             INTEGER NULL,
                        tavern_turns      INTEGER NOT NULL,
                        play_state        INTEGER NOT NULL,
                        truncated         INTEGER NOT NULL,
                        video_status      INTEGER NOT NULL,
                        video_path        TEXT    NULL,
                        video_size_bytes  INTEGER NULL,
                        video_duration_ms INTEGER NULL,
                        starred           INTEGER NOT NULL DEFAULT 0,
                        manual_rating     INTEGER NULL,
                        created_at        TEXT    NOT NULL
                    );
                    INSERT INTO matches (
                        session_id, started_at, started_at_utc, game_type, hero_card_id,
                        tavern_turns, play_state, truncated, video_status, created_at)
                    VALUES (
                        'resume-session', '2026-07-14T09:15:30.0000000-07:00',
                        '2026-07-14T16:15:30.0000000+00:00', 1, 'PRESERVED',
                        11, 1, 0, 0, '2026-07-14T16:15:30.0000000+00:00');
                    """);
            }

            var repo = new SqliteMatchRepository(db);
            await repo.InitializeAsync();
            await repo.InitializeAsync(); // repeat proves the resumed step is idempotent

            var preserved = Assert.Single(await repo.ListMatchesAsync());
            Assert.Equal("resume-session", preserved.SessionId);
            Assert.Equal("PRESERVED", preserved.HeroCardId);

            await using var check = OpenRaw(db);
            Assert.Equal(1L, await check.ExecuteScalarAsync<long>("""
                SELECT COUNT(*) FROM sqlite_master
                WHERE type = 'index' AND name = 'ux_matches_session_id';
                """));
            await Assert.ThrowsAsync<SqliteException>(async () => await check.ExecuteAsync("""
                INSERT INTO matches (
                    session_id, started_at, started_at_utc, game_type, tavern_turns,
                    play_state, truncated, video_status, created_at)
                VALUES (
                    'resume-session', '2026-07-14T10:00:00.0000000-07:00',
                    '2026-07-14T17:00:00.0000000+00:00', 1, 1, 1, 0, 0,
                    '2026-07-14T17:00:00.0000000+00:00');
                """));
            Assert.Equal(1L, await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM matches;"));
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task Initialize_resumes_when_started_at_utc_exists_with_null_rows()
    {
        var db = NewDbPath();
        try
        {
            await using (var raw = OpenRaw(db))
            {
                await raw.ExecuteAsync("""
                    CREATE TABLE schema_version (version INTEGER NOT NULL);
                    INSERT INTO schema_version (version) VALUES (1);
                    CREATE TABLE matches (
                        id                INTEGER PRIMARY KEY AUTOINCREMENT,
                        session_id        TEXT    NULL UNIQUE,
                        started_at        TEXT    NOT NULL,
                        started_at_utc    TEXT    NULL,
                        ended_at          TEXT    NULL,
                        game_type         INTEGER NOT NULL,
                        hero_card_id      TEXT    NULL,
                        place             INTEGER NULL,
                        tavern_turns      INTEGER NOT NULL,
                        play_state        INTEGER NOT NULL,
                        truncated         INTEGER NOT NULL,
                        video_status      INTEGER NOT NULL,
                        video_path        TEXT    NULL,
                        video_size_bytes  INTEGER NULL,
                        video_duration_ms INTEGER NULL,
                        starred           INTEGER NOT NULL DEFAULT 0,
                        manual_rating     INTEGER NULL,
                        created_at        TEXT    NOT NULL
                    );
                    INSERT INTO matches (
                        session_id, started_at, started_at_utc, game_type, hero_card_id,
                        tavern_turns, play_state, truncated, video_status, created_at)
                    VALUES
                        ('resume-utc-1', '2026-07-14T09:15:30.0000000-07:00', NULL, 1, 'FIRST',
                         11, 1, 0, 0, '2026-07-14T16:15:30.0000000+00:00'),
                        ('resume-utc-2', '2026-07-14T18:45:00.0000000+05:30', NULL, 2, 'SECOND',
                         9, 2, 0, 0, '2026-07-14T13:15:00.0000000+00:00');
                    """);
            }

            var repo = new SqliteMatchRepository(db);
            await repo.InitializeAsync();
            await repo.InitializeAsync(); // a completed backfill is an idempotent no-op

            var rows = await repo.ListMatchesAsync();
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, row => row.HeroCardId == "FIRST" && row.SessionId == "resume-utc-1");
            Assert.Contains(rows, row => row.HeroCardId == "SECOND" && row.SessionId == "resume-utc-2");

            await using var check = OpenRaw(db);
            Assert.Equal(0L, await check.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM matches WHERE started_at_utc IS NULL;"));
            var backfilled = (await check.QueryAsync<(string StartedAt, string StartedAtUtc)>(
                "SELECT started_at AS StartedAt, started_at_utc AS StartedAtUtc FROM matches ORDER BY id;"))
                .ToList();
            Assert.Equal(
                DateTimeOffset.Parse(backfilled[0].StartedAt).ToUniversalTime(),
                DateTimeOffset.Parse(backfilled[0].StartedAtUtc));
            Assert.Equal(
                DateTimeOffset.Parse(backfilled[1].StartedAt).ToUniversalTime(),
                DateTimeOffset.Parse(backfilled[1].StartedAtUtc));
        }
        finally { Cleanup(db); }
    }
}
