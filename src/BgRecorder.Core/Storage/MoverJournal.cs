namespace BgRecorder.Core.Storage;

/// <summary>
/// The stage a journaled archive move has reached, so a crash mid-move can be reconciled at startup.
/// The transaction is: write the entry (Copying), copy, verify, flip the DB row to the destination,
/// then delete the source. Source wins if a crash lands before the flip; the destination wins after.
/// </summary>
public enum MoverJournalState
{
    /// <summary>The destination copy is in progress; the source is still authoritative.</summary>
    Copying = 0,

    /// <summary>The copy finished; its hash is being verified against the source.</summary>
    Verifying = 1,

    /// <summary>The copy verified; the database row is being flipped to the destination.</summary>
    Flipping = 2,

    /// <summary>The flip committed; the now-redundant source file is being deleted.</summary>
    Deleting = 3,
}

/// <summary>One in-flight archive move, persisted so startup reconciliation can finish or unwind it.</summary>
public sealed record MoverJournalEntry
{
    public long Id { get; init; }
    public required long MatchId { get; init; }
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }

    /// <summary>The source's content hash, captured before the copy so the copy can be verified.</summary>
    public string? SourceHash { get; init; }

    public required MoverJournalState State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Durable record of in-flight archive moves, used only by the mover and its recovery pass.</summary>
public interface IMoverJournal
{
    /// <summary>Creates the journal storage. Idempotent.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Appends a new in-flight move and returns its id.</summary>
    Task<long> AppendAsync(MoverJournalEntry entry, CancellationToken ct = default);

    /// <summary>Advances an entry to a later stage.</summary>
    Task UpdateStateAsync(long id, MoverJournalState state, CancellationToken ct = default);

    /// <summary>Removes a completed (or unwound) entry.</summary>
    Task RemoveAsync(long id, CancellationToken ct = default);

    /// <summary>All outstanding entries, oldest first — the startup reconciliation work list.</summary>
    Task<IReadOnlyList<MoverJournalEntry>> ListAsync(CancellationToken ct = default);
}
