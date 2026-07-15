namespace BgRecorder.Ui;

/// <summary>The complete payload used to bootstrap the library SPA.</summary>
public sealed record LibrarySnapshot(
    string CoordinatorState,
    IReadOnlyList<LibraryMatchSummary> Matches);

/// <summary>
/// UI-safe match metadata. The database path is intentionally absent: media is addressed through
/// an opaque match-id URL that the native host resolves against trusted repository state.
/// </summary>
public sealed record LibraryMatchSummary(
    long Id,
    DateTimeOffset StartedAt,
    string GameType,
    string? HeroCardId,
    int? Place,
    int TavernTurns,
    string VideoStatus,
    long? VideoSizeBytes,
    long? VideoDurationMs,
    bool Starred,
    int? ManualRating,
    string? MediaUrl);

/// <summary>Selected-match payload, including the persisted seek markers.</summary>
public sealed record LibraryMatchDetail(
    LibraryMatchSummary Match,
    IReadOnlyList<LibraryMarker> Markers);

public sealed record LibraryMarker(string Kind, long AtMs, int TavernTurn);

public sealed record RecorderStateResult(string State);

public sealed record StarredResult(long MatchId, bool Starred);

public sealed record ManualRatingResult(long MatchId, int? Rating);

/// <summary>Optional rating-provider projection. v1 always reports "disabled" with a null rating.</summary>
public sealed record RatingInfoResult(string Health, int? Rating, DateTimeOffset? SampledAt);
