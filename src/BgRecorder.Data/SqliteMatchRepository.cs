using System.Data.Common;
using System.Globalization;
using BgRecorder.Core.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BgRecorder.Data;

/// <summary>
/// SQLite-backed <see cref="IMatchRepository"/>. Connection-per-operation against a fixed db path,
/// WAL journalling, foreign keys enforced, everything parameterized through Dapper.
/// <see cref="DateTimeOffset"/> values are stored as round-trip ("O") ISO 8601 text so both the
/// instant and the offset survive; durations are stored as whole milliseconds.
/// </summary>
public sealed class SqliteMatchRepository : IMatchRepository
{
    /// <summary>Current on-disk schema revision written to the <c>schema_version</c> table.</summary>
    public const int SchemaVersion = 1;

    private readonly string _dbPath;
    private readonly string _connectionString;

    public SqliteMatchRepository(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            ForeignKeys = true,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(SchemaSql, cancellationToken: ct));
        await MigrateSchemaAsync(conn, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Bring an existing <c>matches</c> table up to the current column set. A database created by an
    /// earlier build may predate the <c>session_id</c> / <c>started_at_utc</c> columns; because
    /// <c>CREATE TABLE IF NOT EXISTS</c> leaves such a table untouched, the insert/list/recovery
    /// queries would otherwise fail with "no such column". Column additions are guarded by the
    /// table's actual shape, while constraint creation and backfill are independently idempotent, so
    /// this is safe to resume after an interrupted migration and safe to run on every startup.
    /// </summary>
    private static async Task MigrateSchemaAsync(SqliteConnection conn, CancellationToken ct)
    {
        var columns = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT name FROM pragma_table_info('matches');", cancellationToken: ct)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!columns.Contains("session_id"))
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "ALTER TABLE matches ADD COLUMN session_id TEXT NULL;", cancellationToken: ct));
        }

        // ADD COLUMN can't carry an inline UNIQUE, so use one named partial index as the canonical
        // constraint for both fresh and migrated schemas (NULLs remain exempt). Keep this step
        // independent from the ADD COLUMN: if the process stopped after ALTER TABLE, the next startup
        // still creates the index.
        await conn.ExecuteAsync(new CommandDefinition(
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_matches_session_id ON matches(session_id) WHERE session_id IS NOT NULL;",
            cancellationToken: ct));

        if (!columns.Contains("started_at_utc"))
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "ALTER TABLE matches ADD COLUMN started_at_utc TEXT NULL;", cancellationToken: ct));
        }

        // Backfill independently from adding the column so an interrupted prior migration resumes.
        // The WHERE guard also makes this a cheap, idempotent no-op once every row is populated.
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT id, started_at FROM matches WHERE started_at_utc IS NULL;", cancellationToken: ct));
        foreach (var row in rows)
        {
            var utc = ParseTimestamp((string)row.started_at)
                .ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE matches SET started_at_utc = @utc WHERE id = @id;",
                new { utc, id = (long)row.id }, cancellationToken: ct));
        }
    }

    public async Task<long> InsertMatchAsync(
        MatchRecord match, IReadOnlyList<MarkerRecord> markers, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Idempotency: a recording carries a stable SessionId (the staging folder). If a crash
        // recovery re-runs the same session, return the existing row instead of duplicating it.
        if (match.SessionId is not null)
        {
            var existing = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT id FROM matches WHERE session_id = @session_id;",
                new { session_id = match.SessionId }, tx, cancellationToken: ct));
            if (existing is { } existingId)
            {
                await tx.CommitAsync(ct);
                return existingId;
            }
        }

        var id = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(InsertMatchSql, ToMatchParameters(match), tx, cancellationToken: ct));

        foreach (var marker in markers)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                InsertMarkerSql,
                new
                {
                    match_id = id,
                    kind = (int)marker.Kind,
                    at_ms = marker.AtMs,
                    tavern_turn = marker.TavernTurn,
                },
                tx,
                cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<bool> MatchExistsBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        await using var conn = await OpenConnectionAsync(ct);
        var found = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT 1 FROM matches WHERE session_id = @session_id LIMIT 1;",
            new { session_id = sessionId }, cancellationToken: ct));
        return found is not null;
    }

    public async Task UpdateVideoStatusAsync(long matchId, VideoStatus status, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE matches SET video_status = @status WHERE id = @id;",
            new { status = (int)status, id = matchId },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<MatchRecord>> ListMatchesAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<MatchRow>(new CommandDefinition(ListMatchesSql, cancellationToken: ct));
        return rows.Select(MapRow).ToList();
    }

    public async Task<MatchDetailRecord?> GetMatchAsync(long matchId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var match = await conn.QuerySingleOrDefaultAsync<MatchRow>(new CommandDefinition(
            GetMatchSql,
            new { id = matchId },
            tx,
            cancellationToken: ct));

        if (match is null)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        var markerRows = await conn.QueryAsync<MarkerRow>(new CommandDefinition(
            GetMarkersSql,
            new { match_id = matchId },
            tx,
            cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new MatchDetailRecord(MapRow(match), markerRows.Select(MapMarker).ToList());
    }

    public async Task UpdateStarredAsync(long matchId, bool starred, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            UpdateStarredSql,
            new { id = matchId, starred = starred ? 1 : 0 },
            tx,
            cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    public async Task UpdateVideoLocationAsync(long matchId, string videoPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE matches SET video_path = @video_path WHERE id = @id;",
            new { id = matchId, video_path = videoPath },
            cancellationToken: ct));
    }

    public async Task DeleteMatchAsync(long matchId, CancellationToken ct = default)
    {
        // Markers are removed by the ON DELETE CASCADE foreign key (foreign_keys is on per connection).
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM matches WHERE id = @id;",
            new { id = matchId },
            cancellationToken: ct));
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct);
            // WAL is persisted in the file header; re-issuing it per connection is cheap and
            // guarantees the mode even on a freshly created database. foreign_keys is set by the
            // connection string (ForeignKeys=true) since it is a per-connection pragma.
            await using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            await pragma.ExecuteNonQueryAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private static object ToMatchParameters(MatchRecord match) => new
    {
        session_id = match.SessionId,
        started_at = match.StartedAt.ToString("O", CultureInfo.InvariantCulture),
        // A normalized UTC instant ("...Z") whose lexical order equals chronological order, so the
        // library list sorts correctly even when rows carry different UTC offsets (DST, travel).
        started_at_utc = match.StartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ended_at = match.EndedAt?.ToString("O", CultureInfo.InvariantCulture),
        game_type = (int)match.GameType,
        hero_card_id = match.HeroCardId,
        place = match.Place,
        tavern_turns = match.TavernTurns,
        play_state = (int)match.PlayState,
        truncated = match.Truncated ? 1 : 0,
        video_status = (int)match.VideoStatus,
        video_path = match.VideoPath,
        video_size_bytes = match.VideoSizeBytes,
        video_duration_ms = match.VideoDuration.HasValue
            ? (long?)(long)match.VideoDuration.Value.TotalMilliseconds
            : null,
        starred = match.Starred ? 1 : 0,
        manual_rating = match.ManualRating,
        created_at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
    };

    private static MatchRecord MapRow(MatchRow row) => new()
    {
        Id = row.id,
        SessionId = row.session_id,
        StartedAt = ParseTimestamp(row.started_at),
        EndedAt = row.ended_at is null ? null : ParseTimestamp(row.ended_at),
        GameType = (Core.Events.BgGameType)row.game_type,
        HeroCardId = row.hero_card_id,
        Place = row.place,
        TavernTurns = row.tavern_turns,
        PlayState = (Core.Events.PlayState)row.play_state,
        Truncated = row.truncated != 0,
        VideoStatus = (VideoStatus)row.video_status,
        VideoPath = row.video_path,
        VideoSizeBytes = row.video_size_bytes,
        VideoDuration = row.video_duration_ms is null ? null : TimeSpan.FromMilliseconds(row.video_duration_ms.Value),
        Starred = row.starred != 0,
        ManualRating = row.manual_rating,
    };

    private static MarkerRecord MapMarker(MarkerRow row) => new(
        row.match_id,
        (MarkerKind)row.kind,
        row.at_ms,
        row.tavern_turn);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>Column-shaped read DTO; property names match the schema exactly for Dapper mapping.</summary>
    private sealed class MatchRow
    {
        public long id { get; init; }
        public string? session_id { get; init; }
        public string started_at { get; init; } = "";
        public string? ended_at { get; init; }
        public int game_type { get; init; }
        public string? hero_card_id { get; init; }
        public int? place { get; init; }
        public int tavern_turns { get; init; }
        public int play_state { get; init; }
        public long truncated { get; init; }
        public int video_status { get; init; }
        public string? video_path { get; init; }
        public long? video_size_bytes { get; init; }
        public long? video_duration_ms { get; init; }
        public long starred { get; init; }
        public int? manual_rating { get; init; }
    }

    /// <summary>Column-shaped marker DTO; the id is selected only to define stable tie ordering.</summary>
    private sealed class MarkerRow
    {
        public long id { get; init; }
        public long match_id { get; init; }
        public int kind { get; init; }
        public long at_ms { get; init; }
        public int tavern_turn { get; init; }
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS matches (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id        TEXT    NULL,
            started_at        TEXT    NOT NULL,
            started_at_utc    TEXT    NOT NULL,
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

        CREATE TABLE IF NOT EXISTS markers (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            match_id    INTEGER NOT NULL,
            kind        INTEGER NOT NULL,
            at_ms       INTEGER NOT NULL,
            tavern_turn INTEGER NOT NULL,
            FOREIGN KEY (match_id) REFERENCES matches(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_markers_match_id ON markers(match_id);

        INSERT INTO schema_version (version)
        SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_version);
        """;

    private const string InsertMatchSql = """
        INSERT INTO matches (
            session_id, started_at, started_at_utc, ended_at, game_type, hero_card_id, place,
            tavern_turns, play_state, truncated, video_status, video_path, video_size_bytes,
            video_duration_ms, starred, manual_rating, created_at)
        VALUES (
            @session_id, @started_at, @started_at_utc, @ended_at, @game_type, @hero_card_id, @place,
            @tavern_turns, @play_state, @truncated, @video_status, @video_path, @video_size_bytes,
            @video_duration_ms, @starred, @manual_rating, @created_at)
        RETURNING id;
        """;

    private const string InsertMarkerSql = """
        INSERT INTO markers (match_id, kind, at_ms, tavern_turn)
        VALUES (@match_id, @kind, @at_ms, @tavern_turn);
        """;

    private const string ListMatchesSql = """
        SELECT id, session_id, started_at, ended_at, game_type, hero_card_id, place, tavern_turns,
               play_state, truncated, video_status, video_path, video_size_bytes, video_duration_ms,
               starred, manual_rating
        FROM matches
        ORDER BY started_at_utc DESC, id DESC;
        """;

    private const string GetMatchSql = """
        SELECT id, session_id, started_at, ended_at, game_type, hero_card_id, place, tavern_turns,
               play_state, truncated, video_status, video_path, video_size_bytes, video_duration_ms,
               starred, manual_rating
        FROM matches
        WHERE id = @id;
        """;

    private const string GetMarkersSql = """
        SELECT id, match_id, kind, at_ms, tavern_turn
        FROM markers
        WHERE match_id = @match_id
        ORDER BY at_ms, id;
        """;

    private const string UpdateStarredSql = """
        UPDATE matches
        SET starred = @starred
        WHERE id = @id;
        """;
}
