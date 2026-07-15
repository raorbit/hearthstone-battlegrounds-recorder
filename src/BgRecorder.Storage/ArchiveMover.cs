using BgRecorder.Core.Data;
using BgRecorder.Core.Storage;

namespace BgRecorder.Storage;

/// <summary>The result of a single archive move.</summary>
public sealed record MoveOutcome(bool Success, string? FailureReason);

/// <summary>
/// Relocates a finished recording to an archive volume without ever risking the only good copy. The
/// transaction is journaled — copy → fsync (in <see cref="IFileSystem"/>) → hash verify → journal
/// Flipping → flip the DB row to the destination → delete the source — so a crash or fault at any
/// point leaves exactly one intact, referenced copy. Once the journal reads Flipping the destination
/// is the recovery winner and is never deleted; before that the source is authoritative and a
/// partial destination is discarded. <see cref="ReconcileAsync"/> finishes or unwinds a move a crash
/// interrupted. The destination path must be unique to this match (the engine guarantees that).
/// </summary>
public sealed class ArchiveMover
{
    private readonly IFileSystem _fileSystem;
    private readonly IMoverJournal _journal;
    private readonly IMatchLocationStore _locations;
    private readonly TimeProvider _timeProvider;

    public ArchiveMover(
        IFileSystem fileSystem,
        IMoverJournal journal,
        IMatchLocationStore locations,
        TimeProvider? timeProvider = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _locations = locations ?? throw new ArgumentNullException(nameof(locations));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Diagnostics for deferred/failed recovery steps; never carries a fatal condition.</summary>
    public event Action<string>? Diagnostic;

    public async Task<MoveOutcome> MoveAsync(
        long matchId, string sourcePath, string destinationPath, CancellationToken ct = default)
    {
        if (PathsEqual(sourcePath, destinationPath))
        {
            // Moving a file onto itself would have the source-delete step erase the only copy.
            return new MoveOutcome(false, "The source and destination are the same path.");
        }

        if (!_fileSystem.FileExists(sourcePath))
        {
            return new MoveOutcome(false, "The source recording is missing.");
        }

        var sourceHash = await _fileSystem.ComputeContentHashAsync(sourcePath, ct).ConfigureAwait(false);
        var journalId = await _journal.AppendAsync(
            new MoverJournalEntry
            {
                MatchId = matchId,
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                SourceHash = sourceHash,
                State = MoverJournalState.Copying,
                CreatedAt = _timeProvider.GetUtcNow(),
            },
            ct).ConfigureAwait(false);

        try
        {
            await _fileSystem.CopyAsync(sourcePath, destinationPath, ct).ConfigureAwait(false);

            await _journal.UpdateStateAsync(journalId, MoverJournalState.Verifying, ct).ConfigureAwait(false);
            var destinationHash = await _fileSystem.ComputeContentHashAsync(destinationPath, ct).ConfigureAwait(false);
            if (destinationHash != sourceHash)
            {
                await DiscardBeforeFlipAsync(journalId, destinationPath, ct).ConfigureAwait(false);
                return new MoveOutcome(false, "The archived copy did not verify against its source.");
            }

            // Crossing into "destination wins": everything up to and including this line leaves the
            // journal at or below Verifying, where recovery keeps the source, so it is still safe to
            // discard the destination in the catch below.
            await _journal.UpdateStateAsync(journalId, MoverJournalState.Flipping, ct).ConfigureAwait(false);
        }
        catch
        {
            // Pre-flip fault: the source is still the referenced copy. Discard the partial/unverified
            // destination best-effort so the original fault — not a cleanup error — is what propagates.
            await DiscardBeforeFlipAsync(journalId, destinationPath, ct).ConfigureAwait(false);
            throw;
        }

        // The journal now reads Flipping: the destination is the recovery winner and must NEVER be
        // deleted from here on. If the flip itself faults, the journal entry drives ReconcileAsync to
        // finish the move on the next startup.
        await _locations.UpdateVideoLocationAsync(matchId, destinationPath, ct).ConfigureAwait(false);

        try
        {
            await _journal.UpdateStateAsync(journalId, MoverJournalState.Deleting, ct).ConfigureAwait(false);
            _fileSystem.Delete(sourcePath);
            await _journal.RemoveAsync(journalId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The move already succeeded — the row references the verified destination. A stale source
            // or journal entry is reclaimed by the next ReconcileAsync; it can never cost data.
            Diagnostic?.Invoke(
                $"Archive move for match {matchId} committed; post-flip cleanup deferred to recovery: {ex.Message}");
        }

        return new MoveOutcome(true, null);
    }

    /// <summary>Finishes or unwinds every move a crash left in the journal. Safe to run on any startup.</summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        foreach (var entry in await _journal.ListAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await ReconcileEntryAsync(entry, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Isolate failures so one entry (e.g. a drive that is still offline) cannot strand the
                // rest; its journal entry is left in place and retried on the next startup.
                Diagnostic?.Invoke($"Archive move recovery for match {entry.MatchId} deferred: {ex.Message}");
            }
        }
    }

    private async Task ReconcileEntryAsync(MoverJournalEntry entry, CancellationToken ct)
    {
        switch (entry.State)
        {
            case MoverJournalState.Copying:
            case MoverJournalState.Verifying:
                // Interrupted before the flip: the source is authoritative and still referenced.
                // Discard the possibly-partial destination and forget the move (source wins).
                _fileSystem.Delete(entry.DestinationPath);
                await _journal.RemoveAsync(entry.Id, ct).ConfigureAwait(false);
                break;

            case MoverJournalState.Flipping:
            case MoverJournalState.Deleting:
                if (_fileSystem.FileExists(entry.DestinationPath))
                {
                    // The verified destination survives — complete the move idempotently (dest wins).
                    await _locations.UpdateVideoLocationAsync(entry.MatchId, entry.DestinationPath, ct).ConfigureAwait(false);
                    _fileSystem.Delete(entry.SourcePath);
                    await _journal.RemoveAsync(entry.Id, ct).ConfigureAwait(false);
                }
                else
                {
                    // The destination is not reachable (archive drive offline, or the copy was lost).
                    // Never delete the source or drop the journal here — leave the move for a later
                    // reconciliation once the drive is back, so the source is never the last casualty.
                    Diagnostic?.Invoke(
                        $"Archive move recovery for match {entry.MatchId} deferred: destination not reachable.");
                }

                break;
        }
    }

    private async Task DiscardBeforeFlipAsync(long journalId, string destinationPath, CancellationToken ct)
    {
        try { _fileSystem.Delete(destinationPath); } catch { /* best effort; the source is intact */ }
        try { await _journal.RemoveAsync(journalId, ct).ConfigureAwait(false); } catch { /* best effort */ }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
