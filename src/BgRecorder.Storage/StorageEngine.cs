using BgRecorder.Core.Data;
using BgRecorder.Core.Storage;

namespace BgRecorder.Storage;

/// <summary>What one enforcement pass did.</summary>
public sealed record EnforcementReport(int MovesExecuted, int DeletesExecuted, bool RecordingBelowFloor);

/// <summary>
/// Ties the pieces together: it reads the library, projects it onto the configured volumes (with live
/// free space from <see cref="IFreeSpaceProbe"/>), asks <see cref="IRetentionPolicy"/> for a plan, then
/// executes it — lossless moves through the journaled <see cref="ArchiveMover"/> and deletions of the
/// oldest unstarred recordings. <see cref="ReconcileAsync"/> runs on startup to finish any move a crash
/// interrupted. All the data-loss-critical decisions live in the policy and mover; this is the wiring.
/// </summary>
public sealed class StorageEngine
{
    private readonly IMatchStore _matches;
    private readonly IRetentionPolicy _policy;
    private readonly ArchiveMover _mover;
    private readonly IFreeSpaceProbe _freeSpace;
    private readonly IFileSystem _fileSystem;
    private readonly string _recordingDir;
    private readonly StorageOptions _options;

    public StorageEngine(
        IMatchStore matches,
        IRetentionPolicy policy,
        ArchiveMover mover,
        IFreeSpaceProbe freeSpace,
        IFileSystem fileSystem,
        string recordingDir,
        StorageOptions options)
    {
        _matches = matches ?? throw new ArgumentNullException(nameof(matches));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _mover = mover ?? throw new ArgumentNullException(nameof(mover));
        _freeSpace = freeSpace ?? throw new ArgumentNullException(nameof(freeSpace));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _recordingDir = recordingDir ?? throw new ArgumentNullException(nameof(recordingDir));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public event Action<string>? Diagnostic;

    /// <summary>Finishes any crash-interrupted archive move. Call once at startup before enforcing.</summary>
    public Task ReconcileAsync(CancellationToken ct = default) => _mover.ReconcileAsync(ct);

    /// <summary>Brings the library within its caps and reserve floors for the current state.</summary>
    public async Task<EnforcementReport> EnforceAsync(CancellationToken ct = default)
    {
        var records = await _matches.ListMatchesAsync(ct).ConfigureAwait(false);
        var byId = records.ToDictionary(r => r.Id);

        var (volumes, recordingOnline) = BuildVolumes();
        if (!recordingOnline)
        {
            // The recording drive's free space could not be read. A phantom "0 bytes free" would look
            // like a low-space emergency and delete recordings, so refuse to act until we can measure.
            Diagnostic?.Invoke("Retention skipped: the recording volume's free space could not be read.");
            return new EnforcementReport(0, 0, false);
        }

        var stored = new List<StoredMatch>();
        foreach (var record in records)
        {
            var volumeId = ResolveVolumeId(record, volumes);
            if (volumeId is null)
            {
                continue; // no playable file, or it lives outside every managed volume
            }
            stored.Add(new StoredMatch
            {
                MatchId = record.Id,
                VolumeId = volumeId,
                SizeBytes = record.VideoSizeBytes ?? 0,
                Starred = record.Starred,
                FinishedAt = record.EndedAt ?? record.StartedAt,
            });
        }

        var plan = _policy.Plan(new StorageState
        {
            Volumes = volumes,
            Matches = stored,
            HotSetSize = _options.HotSetSize,
            TotalCapBytes = _options.TotalCapBytes,
        });

        var moves = 0;
        foreach (var move in plan.Moves)
        {
            if (!byId.TryGetValue(move.MatchId, out var record) || record.VideoPath is null)
            {
                continue;
            }
            var destination = Path.Combine(move.ToVolumeId, Path.GetFileName(record.VideoPath));
            var outcome = await _mover.MoveAsync(move.MatchId, record.VideoPath, destination, ct).ConfigureAwait(false);
            if (outcome.Success)
            {
                moves++;
            }
            else
            {
                Diagnostic?.Invoke($"Archive move for match {move.MatchId} was skipped: {outcome.FailureReason}");
            }
        }

        var deletes = 0;
        foreach (var delete in plan.Deletes)
        {
            if (byId.TryGetValue(delete.MatchId, out var record) && record.VideoPath is not null)
            {
                _fileSystem.Delete(record.VideoPath);
            }
            await _matches.DeleteMatchAsync(delete.MatchId, ct).ConfigureAwait(false);
            deletes++;
        }

        return new EnforcementReport(moves, deletes, plan.RecordingBelowFloor);
    }

    private (IReadOnlyList<ManagedVolume> Volumes, bool RecordingOnline) BuildVolumes()
    {
        var (recordingOnline, recordingFree) = Probe(_recordingDir);
        var volumes = new List<ManagedVolume>
        {
            new()
            {
                Id = NormalizeDir(_recordingDir),
                Role = VolumeRole.Recording,
                CapBytes = _options.RecordingCapBytes,
                ReserveBytes = _options.RecordingReserveBytes,
                FreeBytes = recordingFree,
                IsOnline = recordingOnline,
            },
        };

        foreach (var archive in _options.ArchiveVolumes)
        {
            var (online, free) = Probe(archive.Directory);
            volumes.Add(new ManagedVolume
            {
                Id = NormalizeDir(archive.Directory),
                Role = VolumeRole.Archive,
                CapBytes = archive.CapBytes,
                ReserveBytes = archive.ReserveBytes,
                FreeBytes = free,
                Priority = archive.Priority,
                IsOnline = online,
            });
        }

        return (volumes, recordingOnline);
    }

    private static string? ResolveVolumeId(MatchRecord record, IReadOnlyList<ManagedVolume> volumes)
    {
        if (record.VideoPath is null || record.VideoStatus == VideoStatus.Missing)
        {
            return null;
        }

        string matchDir;
        try
        {
            matchDir = NormalizeDir(Path.GetDirectoryName(Path.GetFullPath(record.VideoPath)) ?? string.Empty);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // Match the most-specific (longest) volume so a match under an archive nested inside the
        // recording folder is attributed to the archive, not the recording tier that also contains it.
        return volumes
            .Where(v => IsUnder(matchDir, v.Id))
            .OrderByDescending(v => v.Id.Length)
            .FirstOrDefault()?.Id;
    }

    private (bool Online, long Free) Probe(string directory)
    {
        try
        {
            return (true, _freeSpace.GetAvailableFreeBytes(directory));
        }
        catch
        {
            return (false, 0);
        }
    }

    private static string NormalizeDir(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

    private static bool IsUnder(string childDir, string volumeDir) =>
        string.Equals(childDir, volumeDir, StringComparison.OrdinalIgnoreCase) ||
        childDir.StartsWith(volumeDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
