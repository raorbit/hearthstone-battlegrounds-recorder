using BgRecorder.Core.Data;
using BgRecorder.Core.Storage;

namespace BgRecorder.Storage;

/// <summary>The result of a single archive move.</summary>
public sealed record MoveOutcome(bool Success, string? FailureReason);

/// <summary>
/// Relocates a finished recording to an archive volume without ever risking the only good copy. The
/// transaction is journaled — copy → fsync (in <see cref="IFileSystem"/>) → hash verify → flip the DB
/// row to the destination → delete the source — so a crash at any point leaves exactly one intact,
/// referenced copy. <see cref="ReconcileAsync"/> finishes or unwinds a move interrupted by a crash:
/// the source wins before the DB flip, the (verified) destination wins after it.
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

    public async Task<MoveOutcome> MoveAsync(
        long matchId, string sourcePath, string destinationPath, CancellationToken ct = default)
    {
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
                // A bad copy: the source is untouched and still referenced. Drop the destination.
                _fileSystem.Delete(destinationPath);
                await _journal.RemoveAsync(journalId, ct).ConfigureAwait(false);
                return new MoveOutcome(false, "The archived copy did not verify against its source.");
            }

            await _journal.UpdateStateAsync(journalId, MoverJournalState.Flipping, ct).ConfigureAwait(false);
            await _locations.UpdateVideoLocationAsync(matchId, destinationPath, ct).ConfigureAwait(false);

            await _journal.UpdateStateAsync(journalId, MoverJournalState.Deleting, ct).ConfigureAwait(false);
            _fileSystem.Delete(sourcePath);

            await _journal.RemoveAsync(journalId, ct).ConfigureAwait(false);
            return new MoveOutcome(true, null);
        }
        catch
        {
            // The failure landed before the DB flip, so the source is still the referenced copy.
            // Discard the partial/unverified destination and the journal entry, then surface the error.
            _fileSystem.Delete(destinationPath);
            await _journal.RemoveAsync(journalId, ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Finishes or unwinds every move a crash left in the journal. Safe to run on any startup.</summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        foreach (var entry in await _journal.ListAsync(ct).ConfigureAwait(false))
        {
            switch (entry.State)
            {
                case MoverJournalState.Copying:
                case MoverJournalState.Verifying:
                    // Interrupted before the DB flip: the source is authoritative and still referenced.
                    // Discard the possibly-partial destination and forget the move (source wins).
                    _fileSystem.Delete(entry.DestinationPath);
                    await _journal.RemoveAsync(entry.Id, ct).ConfigureAwait(false);
                    break;

                case MoverJournalState.Flipping:
                case MoverJournalState.Deleting:
                    // The copy verified before the crash; complete the move idempotently (destination
                    // wins). The flip and the source delete both no-op if they already happened.
                    await _locations.UpdateVideoLocationAsync(entry.MatchId, entry.DestinationPath, ct).ConfigureAwait(false);
                    _fileSystem.Delete(entry.SourcePath);
                    await _journal.RemoveAsync(entry.Id, ct).ConfigureAwait(false);
                    break;
            }
        }
    }
}
