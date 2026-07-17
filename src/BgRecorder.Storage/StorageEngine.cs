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
public sealed class StorageEngine : IStoragePlanner
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
        var context = await BuildPlanAsync(_options, ct).ConfigureAwait(false);
        if (!context.RecordingOnline)
        {
            // The recording drive's free space could not be read. A phantom "0 bytes free" would look
            // like a low-space emergency and delete recordings, so refuse to act until we can measure.
            Diagnostic?.Invoke("Retention skipped: the recording volume's free space could not be read.");
            return new EnforcementReport(0, 0, false);
        }

        var plan = context.Plan;
        var byId = context.ById;

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
            if (byId.TryGetValue(delete.MatchId, out var record))
            {
                if (record.VideoPath is not null)
                {
                    _fileSystem.Delete(record.VideoPath);
                }
                // Delete the thumbnail sibling too, matching the manual-delete path — otherwise every
                // eviction would leave an orphaned .bmp the row no longer references.
                if (record.ThumbnailPath is not null)
                {
                    _fileSystem.Delete(record.ThumbnailPath);
                }
            }
            await _matches.DeleteMatchAsync(delete.MatchId, ct).ConfigureAwait(false);
            deletes++;
        }

        return new EnforcementReport(moves, deletes, plan.RecordingBelowFloor);
    }

    /// <summary>
    /// Computes what retention would do right now — per-volume usage plus the moves/deletes the current
    /// state implies — without executing anything. Shares the exact projection <see cref="EnforceAsync"/>
    /// runs, so the preview matches what the next enforcement pass would actually do.
    /// </summary>
    public Task<StoragePreview> PreviewAsync(CancellationToken ct = default) => PreviewAsync(_options, ct);

    /// <summary>
    /// Preview under <paramref name="proposed"/> options — the same projection, run against caps that are
    /// not (yet) in force. Enforcement is untouched: it always runs the constructed options, so this can
    /// never make the engine act on unsaved numbers.
    /// </summary>
    public async Task<StoragePreview> PreviewAsync(StorageOptions proposed, CancellationToken ct = default)
    {
        var context = await BuildPlanAsync(proposed, ct).ConfigureAwait(false);

        var byVolume = context.Stored
            .GroupBy(s => s.VolumeId)
            .ToDictionary(g => g.Key, g => (Bytes: g.Sum(s => s.SizeBytes), Count: g.Count()));

        var volumes = context.Volumes.Select(v =>
        {
            byVolume.TryGetValue(v.Id, out var usage);
            return new VolumeUsage(v.Role, usage.Bytes, v.FreeBytes, v.CapBytes, v.IsOnline, usage.Count);
        }).ToList();

        return new StoragePreview(
            volumes,
            context.Plan.Moves.Select(m => new PlannedEviction(m.MatchId, m.SizeBytes)).ToList(),
            context.Plan.Deletes.Select(d => new PlannedEviction(d.MatchId, d.SizeBytes)).ToList(),
            context.Plan.RecordingBelowFloor);
    }

    /// <summary>
    /// Reads the library, projects it onto the configured volumes with live free space, and asks the
    /// policy for a plan — the shared front half of both <see cref="EnforceAsync"/> and
    /// <see cref="PreviewAsync"/>. When the recording drive's free space cannot be read the plan is left
    /// empty (never guess a low-space emergency), but the volume projection is still returned for preview.
    /// </summary>
    private async Task<PlanContext> BuildPlanAsync(StorageOptions options, CancellationToken ct)
    {
        var records = await _matches.ListMatchesAsync(ct).ConfigureAwait(false);
        var byId = records.ToDictionary(r => r.Id);

        var (volumes, recordingOnline) = BuildVolumes(options);

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

        var plan = recordingOnline
            ? _policy.Plan(new StorageState
            {
                Volumes = volumes,
                Matches = stored,
                HotSetSize = options.HotSetSize,
                TotalCapBytes = options.TotalCapBytes,
            })
            : new RetentionPlan();

        return new PlanContext(plan, volumes, stored, byId, recordingOnline);
    }

    private sealed record PlanContext(
        RetentionPlan Plan,
        IReadOnlyList<ManagedVolume> Volumes,
        List<StoredMatch> Stored,
        Dictionary<long, MatchRecord> ById,
        bool RecordingOnline);

    private (IReadOnlyList<ManagedVolume> Volumes, bool RecordingOnline) BuildVolumes(StorageOptions options)
    {
        var (recordingOnline, recordingFree) = Probe(_recordingDir);
        var volumes = new List<ManagedVolume>
        {
            new()
            {
                Id = NormalizeDir(_recordingDir),
                Role = VolumeRole.Recording,
                CapBytes = options.RecordingCapBytes,
                ReserveBytes = options.RecordingReserveBytes,
                FreeBytes = recordingFree,
                IsOnline = recordingOnline,
            },
        };

        foreach (var archive in options.ArchiveVolumes)
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
