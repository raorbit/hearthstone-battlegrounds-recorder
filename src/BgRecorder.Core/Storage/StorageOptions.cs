namespace BgRecorder.Core.Storage;

/// <summary>A user-configured archive drive: where overflow recordings are moved, and its limits.</summary>
public sealed record ArchiveVolumeOptions
{
    /// <summary>The managed folder on the archive drive.</summary>
    public required string Directory { get; init; }

    /// <summary>User-intent cap on our content on this drive, in bytes.</summary>
    public required long CapBytes { get; init; }

    /// <summary>Free-space floor the mover never breaches on this drive. Default 5 GiB.</summary>
    public long ReserveBytes { get; init; } = 5L << 30;

    /// <summary>Archive fill order; lower runs first.</summary>
    public int Priority { get; init; }
}

/// <summary>
/// The retention/archive configuration (part of <see cref="AppSettings"/>). Defaults keep the single
/// recording drive bounded; archive drives are opt-in. Caps are user intent, free space is authoritative.
/// </summary>
public sealed record StorageOptions
{
    /// <summary>User-intent cap on the recording drive, in bytes. Default 200 GiB.</summary>
    public long RecordingCapBytes { get; init; } = 200L << 30;

    /// <summary>Free-space floor on the recording drive, in bytes. Default 10 GiB.</summary>
    public long RecordingReserveBytes { get; init; } = 10L << 30;

    /// <summary>Priority-ordered archive drives; empty by default (single-drive behaviour).</summary>
    public IReadOnlyList<ArchiveVolumeOptions> ArchiveVolumes { get; init; } = [];

    /// <summary>The newest K matches always stay on the recording drive. Default 5.</summary>
    public int HotSetSize { get; init; } = 5;

    /// <summary>Optional whole-library cap in bytes; null leaves only the per-drive caps in force.</summary>
    public long? TotalCapBytes { get; init; }
}
