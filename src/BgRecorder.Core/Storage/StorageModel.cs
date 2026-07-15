namespace BgRecorder.Core.Storage;

/// <summary>A managed drive's role in the retention tiers.</summary>
public enum VolumeRole
{
    /// <summary>The single drive new recordings always land on (the prototype's Max-storage slider).</summary>
    Recording = 0,

    /// <summary>A priority-ordered overflow drive that finished recordings are moved to losslessly.</summary>
    Archive = 1,
}

/// <summary>
/// A drive the retention engine manages. Caps are user intent; real free space is authoritative, so
/// both a cap and a reserve floor bound how much the engine will place here (see the retention spec
/// in docs/implementation-plan.md).
/// </summary>
public sealed record ManagedVolume
{
    public required string Id { get; init; }
    public required VolumeRole Role { get; init; }

    /// <summary>User-intent cap on <em>our</em> content on this volume, in bytes.</summary>
    public required long CapBytes { get; init; }

    /// <summary>The engine never lets real free space on this volume drop below this floor.</summary>
    public required long ReserveBytes { get; init; }

    /// <summary>Real free space on the physical volume right now — the authoritative budget input.</summary>
    public required long FreeBytes { get; init; }

    /// <summary>Archive ordering; lower runs first. Ignored for the recording volume.</summary>
    public int Priority { get; init; }

    /// <summary>An unplugged archive can neither receive moves nor serve playback; its rows go offline.</summary>
    public bool IsOnline { get; init; } = true;
}

/// <summary>A finished recording as the retention engine sees it (metadata only, no file handles).</summary>
public sealed record StoredMatch
{
    public required long MatchId { get; init; }

    /// <summary>The volume the match currently lives on.</summary>
    public required string VolumeId { get; init; }

    public required long SizeBytes { get; init; }

    /// <summary>Starred matches are inviolable: moved losslessly, never deleted to reclaim space.</summary>
    public bool Starred { get; init; }

    /// <summary>Ordering key for oldest-finished-first eviction and newest-first hot-set pinning.</summary>
    public required DateTimeOffset FinishedAt { get; init; }
}

/// <summary>The complete input to a retention decision.</summary>
public sealed record StorageState
{
    public required IReadOnlyList<ManagedVolume> Volumes { get; init; }
    public required IReadOnlyList<StoredMatch> Matches { get; init; }

    /// <summary>The newest K matches always stay on the recording volume (default 5).</summary>
    public int HotSetSize { get; init; } = 5;

    /// <summary>Optional whole-library cap; null leaves the library bounded only by per-volume caps.</summary>
    public long? TotalCapBytes { get; init; }
}

/// <summary>Relocate a match from one volume to another (lossless; starred matches included).</summary>
public sealed record RetentionMove(long MatchId, string FromVolumeId, string ToVolumeId, long SizeBytes);

public enum RetentionDeleteReason
{
    /// <summary>The recording volume is over its cap or reserve floor and no archive can take the move.</summary>
    RecordingVolumeOverBudget = 0,

    /// <summary>The whole library is over the optional total cap.</summary>
    TotalLibraryOverCap = 1,
}

/// <summary>Permanently remove a match's file to reclaim space. Only ever an unstarred match.</summary>
public sealed record RetentionDelete(long MatchId, string VolumeId, long SizeBytes, RetentionDeleteReason Reason);

/// <summary>
/// The engine's decision for the current state: a batch of lossless moves, then deletions. Moves
/// are applied before deletions so a move's freed source space is available. When the recording
/// volume is still below its reserve floor but everything evictable is starred, nothing is deleted
/// and <see cref="RecordingBelowFloor"/> is raised so the host notifies and refuses to arm the next
/// match (the current recording always finishes).
/// </summary>
public sealed record RetentionPlan
{
    public IReadOnlyList<RetentionMove> Moves { get; init; } = [];
    public IReadOnlyList<RetentionDelete> Deletes { get; init; } = [];
    public bool RecordingBelowFloor { get; init; }
}

/// <summary>Pure retention decision: given the current storage state, compute the plan. No I/O.</summary>
public interface IRetentionPolicy
{
    RetentionPlan Plan(StorageState state);
}
