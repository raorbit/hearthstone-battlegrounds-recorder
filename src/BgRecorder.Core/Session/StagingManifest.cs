using BgRecorder.Core.Events;

namespace BgRecorder.Core.Session;

/// <summary>
/// Crash-recovery sidecar written next to the staged video/audio while a recording runs,
/// updated as events arrive. On startup, an orphaned manifest (no clean-finalize flag) means
/// the process died mid-recording: mux what exists and register the match as Incomplete.
/// </summary>
public sealed record StagingManifest
{
    public required string SessionId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required string VideoPath { get; init; }
    public required string AudioPath { get; init; }
    public DateTimeOffset? VideoFirstFrameWallClock { get; init; }
    public DateTimeOffset? AudioFirstSampleWallClock { get; init; }
    /// <summary>Events seen so far, so a crash still yields hero/turn/marker metadata.</summary>
    public IReadOnlyList<GameEvent> Events { get; init; } = [];
    /// <summary>Set as the very last step of a clean finalize; its absence marks an orphan.</summary>
    public bool FinalizedCleanly { get; init; }
}
