using System.Globalization;
using BgRecorder.Core.Storage;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BgRecorder.Data;

/// <summary>
/// SQLite-backed <see cref="IMoverJournal"/>. Shares the library database file (a separate
/// <c>mover_journal</c> table) so a move and its journal advance under the same durable store.
/// Connection-per-operation with WAL, matching <see cref="SqliteMatchRepository"/>.
/// </summary>
public sealed class SqliteMoverJournal : IMoverJournal
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public SqliteMoverJournal(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(SchemaSql, cancellationToken: ct));
    }

    public async Task<long> AppendAsync(MoverJournalEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            InsertSql,
            new
            {
                match_id = entry.MatchId,
                source_path = entry.SourcePath,
                dest_path = entry.DestinationPath,
                source_hash = entry.SourceHash,
                state = (int)entry.State,
                created_at = entry.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken: ct));
    }

    public async Task UpdateStateAsync(long id, MoverJournalState state, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE mover_journal SET state = @state WHERE id = @id;",
            new { id, state = (int)state },
            cancellationToken: ct));
    }

    public async Task RemoveAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM mover_journal WHERE id = @id;",
            new { id },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<MoverJournalEntry>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<JournalRow>(new CommandDefinition(ListSql, cancellationToken: ct));
        return rows.Select(MapRow).ToList();
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct);
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

    private static MoverJournalEntry MapRow(JournalRow row) => new()
    {
        Id = row.id,
        MatchId = row.match_id,
        SourcePath = row.source_path,
        DestinationPath = row.dest_path,
        SourceHash = row.source_hash,
        State = (MoverJournalState)row.state,
        CreatedAt = DateTimeOffset.Parse(row.created_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
    };

    private sealed class JournalRow
    {
        public long id { get; init; }
        public long match_id { get; init; }
        public string source_path { get; init; } = "";
        public string dest_path { get; init; } = "";
        public string? source_hash { get; init; }
        public int state { get; init; }
        public string created_at { get; init; } = "";
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS mover_journal (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            match_id    INTEGER NOT NULL,
            source_path TEXT    NOT NULL,
            dest_path   TEXT    NOT NULL,
            source_hash TEXT    NULL,
            state       INTEGER NOT NULL,
            created_at  TEXT    NOT NULL
        );
        """;

    private const string InsertSql = """
        INSERT INTO mover_journal (match_id, source_path, dest_path, source_hash, state, created_at)
        VALUES (@match_id, @source_path, @dest_path, @source_hash, @state, @created_at)
        RETURNING id;
        """;

    private const string ListSql = """
        SELECT id, match_id, source_path, dest_path, source_hash, state, created_at
        FROM mover_journal
        ORDER BY id;
        """;
}
