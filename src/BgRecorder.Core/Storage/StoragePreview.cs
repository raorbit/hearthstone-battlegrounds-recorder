namespace BgRecorder.Core.Storage;

/// <summary>
/// A read-only snapshot of managed-storage usage plus the retention plan that WOULD run right now,
/// computed without executing anything. Powers the storage tab's usage bars and eviction preview.
/// </summary>
public sealed record StoragePreview(
    IReadOnlyList<VolumeUsage> Volumes,
    IReadOnlyList<PlannedEviction> PlannedMoves,
    IReadOnlyList<PlannedEviction> PlannedDeletes,
    bool RecordingBelowFloor);

/// <summary>Our managed content on one volume, with its live free space and configured limits.</summary>
public sealed record VolumeUsage(
    VolumeRole Role,
    long UsedBytes,
    long FreeBytes,
    long CapBytes,
    bool IsOnline,
    int MatchCount);

/// <summary>A single match the retention plan would move or delete, for preview display.</summary>
public sealed record PlannedEviction(long MatchId, long SizeBytes);

/// <summary>Computes what retention would do right now, without performing any move or deletion.</summary>
public interface IStoragePlanner
{
    Task<StoragePreview> PreviewAsync(CancellationToken ct = default);
}
