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
    }

    public async Task<long> InsertMatchAsync(
        MatchRecord match, IReadOnlyList<MarkerRecord> markers, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

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
        started_at = match.StartedAt.ToString("O", CultureInfo.InvariantCulture),
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

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>Column-shaped read DTO; property names match the schema exactly for Dapper mapping.</summary>
    private sealed class MatchRow
    {
        public long id { get; init; }
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

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS matches (
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
            started_at, ended_at, game_type, hero_card_id, place, tavern_turns, play_state,
            truncated, video_status, video_path, video_size_bytes, video_duration_ms,
            starred, manual_rating, created_at)
        VALUES (
            @started_at, @ended_at, @game_type, @hero_card_id, @place, @tavern_turns, @play_state,
            @truncated, @video_status, @video_path, @video_size_bytes, @video_duration_ms,
            @starred, @manual_rating, @created_at)
        RETURNING id;
        """;

    private const string InsertMarkerSql = """
        INSERT INTO markers (match_id, kind, at_ms, tavern_turn)
        VALUES (@match_id, @kind, @at_ms, @tavern_turn);
        """;

    private const string ListMatchesSql = """
        SELECT id, started_at, ended_at, game_type, hero_card_id, place, tavern_turns, play_state,
               truncated, video_status, video_path, video_size_bytes, video_duration_ms,
               starred, manual_rating
        FROM matches
        ORDER BY started_at DESC, id DESC;
        """;
}
