using BgRecorder.Core.Storage;

namespace BgRecorder.Storage;

/// <summary>
/// The pure retention algorithm from docs/implementation-plan.md. Given the current storage state it
/// computes lossless archive moves, then unstarred deletions, to bring the recording volume within
/// its cap and reserve floor — keeping the newest K matches pinned, treating starred matches as
/// inviolable, and honouring an optional whole-library cap. No I/O: the engine executes the plan.
/// </summary>
public sealed class RetentionPolicy : IRetentionPolicy
{
    public RetentionPlan Plan(StorageState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // A malformed volume set — duplicate ids, or not exactly one recording volume — is ambiguous.
        // A data-loss-critical planner must never guess or crash on it, so it plans nothing and leaves
        // the composition layer to surface the misconfiguration.
        if (state.Volumes.Select(v => v.Id).Distinct().Count() != state.Volumes.Count)
        {
            return new RetentionPlan();
        }

        var recordingVolumes = state.Volumes.Where(v => v.Role == VolumeRole.Recording).ToList();
        if (recordingVolumes.Count != 1)
        {
            return new RetentionPlan();
        }

        var recording = recordingVolumes[0];

        // Mutable working copies so a sequence of moves/deletes sees each other's reclaimed space.
        var free = state.Volumes.ToDictionary(v => v.Id, v => v.FreeBytes);
        var used = state.Volumes.ToDictionary(
            v => v.Id,
            v => state.Matches.Where(m => m.VolumeId == v.Id).Sum(m => m.SizeBytes));
        var caps = state.Volumes.ToDictionary(v => v.Id, v => v.CapBytes);
        var reserves = state.Volumes.ToDictionary(v => v.Id, v => v.ReserveBytes);

        var archives = state.Volumes
            .Where(v => v.Role == VolumeRole.Archive && v.IsOnline)
            .OrderBy(v => v.Priority)
            .ToList();

        // Hot set: the newest K matches overall are pinned to the recording volume, never evicted.
        var hotSet = state.Matches
            .OrderByDescending(m => m.FinishedAt)
            .ThenByDescending(m => m.MatchId)
            .Take(Math.Max(0, state.HotSetSize))
            .Select(m => m.MatchId)
            .ToHashSet();

        var moves = new List<RetentionMove>();
        var deletes = new List<RetentionDelete>();
        var evicted = new HashSet<long>();

        bool RecordingOverBudget() =>
            used[recording.Id] > caps[recording.Id] || free[recording.Id] < reserves[recording.Id];

        // Evict oldest-finished-first from the recording volume (excluding the hot set) until it is
        // within cap and reserve. Prefer a lossless archive move; else delete only if unstarred.
        var candidates = state.Matches
            .Where(m => m.VolumeId == recording.Id && !hotSet.Contains(m.MatchId))
            .OrderBy(m => m.FinishedAt)
            .ThenBy(m => m.MatchId)
            .ToList();

        foreach (var match in candidates)
        {
            if (!RecordingOverBudget())
            {
                break;
            }

            var target = archives.FirstOrDefault(a =>
                used[a.Id] + match.SizeBytes <= caps[a.Id] &&
                free[a.Id] - match.SizeBytes >= reserves[a.Id]);

            if (target is not null)
            {
                moves.Add(new RetentionMove(match.MatchId, recording.Id, target.Id, match.SizeBytes));
                Apply(used, free, target.Id, recording.Id, match.SizeBytes);
                evicted.Add(match.MatchId);
            }
            else if (!match.Starred)
            {
                deletes.Add(new RetentionDelete(
                    match.MatchId, recording.Id, match.SizeBytes, RetentionDeleteReason.RecordingVolumeOverBudget));
                used[recording.Id] -= match.SizeBytes;
                free[recording.Id] += match.SizeBytes;
                evicted.Add(match.MatchId);
            }

            // Otherwise the match is starred and no archive can take it — it cannot be evicted; leave
            // it in place and keep scanning older-to-newer for something that can be.
        }

        // The optional whole-library cap: delete oldest-unstarred until the library is under it. Only
        // matches on an online, managed volume are eligible — an offline archive keeps everything
        // (rule 7), and a match on an unmanaged/dropped volume cannot be touched. The hot set and
        // matches already moved/deleted above are skipped (moved matches are preserved on their new
        // volume rather than deleted here) so the plan stays self-consistent.
        if (state.TotalCapBytes is { } totalCap)
        {
            var deletableVolumeIds = state.Volumes
                .Where(v => v.IsOnline)
                .Select(v => v.Id)
                .ToHashSet();

            var globalCandidates = state.Matches
                .Where(m => !m.Starred
                    && !evicted.Contains(m.MatchId)
                    && !hotSet.Contains(m.MatchId)
                    && deletableVolumeIds.Contains(m.VolumeId))
                .OrderBy(m => m.FinishedAt)
                .ThenBy(m => m.MatchId)
                .ToList();

            foreach (var match in globalCandidates)
            {
                if (used.Values.Sum() <= totalCap)
                {
                    break;
                }

                deletes.Add(new RetentionDelete(
                    match.MatchId, match.VolumeId, match.SizeBytes, RetentionDeleteReason.TotalLibraryOverCap));
                used[match.VolumeId] -= match.SizeBytes;
                free[match.VolumeId] += match.SizeBytes;
                evicted.Add(match.MatchId);
            }
        }

        // Rule 5: if the recording volume is still below its reserve floor after everything evictable
        // is exhausted, the host must notify and refuse to arm the next match (the current one always
        // finishes). Being over the soft cap alone — with free space above the floor — does not block.
        var belowFloor = free[recording.Id] < reserves[recording.Id];

        return new RetentionPlan
        {
            Moves = moves,
            Deletes = deletes,
            RecordingBelowFloor = belowFloor,
        };
    }

    private static void Apply(
        Dictionary<string, long> used,
        Dictionary<string, long> free,
        string toVolumeId,
        string fromVolumeId,
        long sizeBytes)
    {
        used[toVolumeId] += sizeBytes;
        free[toVolumeId] -= sizeBytes;
        used[fromVolumeId] -= sizeBytes;
        free[fromVolumeId] += sizeBytes;
    }
}
