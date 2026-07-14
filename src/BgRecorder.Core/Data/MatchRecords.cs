using BgRecorder.Core.Events;

namespace BgRecorder.Core.Data;

public sealed record MatchRecord
{
    public long Id { get; init; }
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

    Task<long> InsertMatchAsync(MatchRecord match, IReadOnlyList<MarkerRecord> markers, CancellationToken ct = default);

    Task UpdateVideoStatusAsync(long matchId, VideoStatus status, CancellationToken ct = default);

    Task<IReadOnlyList<MatchRecord>> ListMatchesAsync(CancellationToken ct = default);
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
