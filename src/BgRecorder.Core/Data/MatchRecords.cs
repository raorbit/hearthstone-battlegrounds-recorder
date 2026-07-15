using BgRecorder.Core.Events;

namespace BgRecorder.Core.Data;

public sealed record MatchRecord
{
    public long Id { get; init; }
    /// <summary>
    /// Stable staging-session identity (the staging folder name). Persisted with a UNIQUE
    /// constraint so a re-run of the same recording — e.g. a crash between the DB commit and the
    /// staging delete, then a startup-recovery pass — is an idempotent no-op rather than a
    /// duplicate row. Null for rows created without a session (e.g. manual imports/tests).
    /// </summary>
    public string? SessionId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public required BgGameType GameType { get; init; }
    public string? HeroCardId { get; init; }
    public int? Place { get; init; }
    public int TavernTurns { get; init; }
    public PlayState PlayState { get; init; }
    public bool Truncated { get; init; }
    public required VideoStatus VideoStatus { get; init; }
    public string? VideoPath { get; init; }
    public long? VideoSizeBytes { get; init; }
    public TimeSpan? VideoDuration { get; init; }
    public bool Starred { get; init; }
    /// <summary>v1 rating is manual entry only (no automatic MMR — M1 licensing decision).</summary>
    public int? ManualRating { get; init; }
}

public enum VideoStatus
{
    /// <summary>Finalized and muxed normally.</summary>
    Complete = 0,

    /// <summary>Recovered from a crash: playable partial VOD.</summary>
    Incomplete = 1,

    /// <summary>Row exists but the file is gone.</summary>
    Missing = 2,
}

public sealed record MarkerRecord(long MatchId, MarkerKind Kind, long AtMs, int TavernTurn);

/// <summary>
/// A library match together with the timeline markers needed by the player. Marker ordering is
/// defined by the repository: ascending video offset, then insertion order for equal offsets.
/// </summary>
public sealed record MatchDetailRecord(MatchRecord Match, IReadOnlyList<MarkerRecord> Markers);

public enum MarkerKind
{
    CombatStart = 0,
    TurnStart = 1,
    MatchEnd = 2,
}

public interface IMatchRepository
{
    /// <summary>Creates/migrates the schema. Idempotent.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Inserts the match and its markers. Idempotent on <see cref="MatchRecord.SessionId"/>: when a
    /// row with the same non-null SessionId already exists, no new row is written and the existing
    /// id is returned, so crash-recovery re-runs cannot duplicate a match.
    /// </summary>
    Task<long> InsertMatchAsync(MatchRecord match, IReadOnlyList<MarkerRecord> markers, CancellationToken ct = default);

    /// <summary>True when a match with the given staging SessionId is already recorded.</summary>
    Task<bool> MatchExistsBySessionAsync(string sessionId, CancellationToken ct = default);

    Task UpdateVideoStatusAsync(long matchId, VideoStatus status, CancellationToken ct = default);

    Task<IReadOnlyList<MatchRecord>> ListMatchesAsync(CancellationToken ct = default);

    /// <summary>Returns one match and its ordered timeline markers, or null when it does not exist.</summary>
    Task<MatchDetailRecord?> GetMatchAsync(long matchId, CancellationToken ct = default);

    /// <summary>Updates only the retention-star flag for the requested match.</summary>
    Task UpdateStarredAsync(long matchId, bool starred, CancellationToken ct = default);
}

/// <summary>What the finalized recording looked like, for marker-offset math and the files row.</summary>
public sealed record RecordingTimeline(
    DateTimeOffset VideoFirstFrameWallClock,
    string FinalVideoPath,
    long SizeBytes,
    TimeSpan Duration);

/// <summary>Assembles parsed events + the recording timeline into a MatchRecord + markers.</summary>
public interface IMatchAssembler
{
    (MatchRecord Match, IReadOnlyList<MarkerRecord> Markers) Assemble(
        IReadOnlyList<GameEvent> events,
        RecordingTimeline? timeline,
        VideoStatus videoStatus);
}
