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
    string? MediaUrl,
    bool IsOffline);

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

/// <summary>
/// Settings projection for the Settings surface (M6). The path fields are shown read-only; the numeric
/// and boolean recording fields are what <c>settings.set</c> writes. The retention/archive configuration
/// is projected separately by the storage RPCs, not here.
/// </summary>
public sealed record SettingsResult(
    string? HearthstoneInstallDir,
    string LibraryDir,
    string StagingDir,
    int Fps,
    int BitrateMbps,
    bool GameOnlyAudio,
    bool MixMicrophone);

/// <summary>Confirms a match was removed from the library.</summary>
public sealed record DeletedResult(long MatchId);

/// <summary>The editable retention caps (M5). Archive drives are surfaced read-only.</summary>
public sealed record StorageSettingsResult(
    long RecordingCapBytes,
    long RecordingReserveBytes,
    int HotSetSize,
    long? TotalCapBytes,
    IReadOnlyList<ArchiveVolumeResult> ArchiveVolumes);

public sealed record ArchiveVolumeResult(string Directory, long CapBytes, long ReserveBytes, int Priority);

/// <summary>
/// The storage tab's usage bars and eviction preview: per-volume managed usage plus the moves and
/// deletes retention would perform right now (nothing is executed). Volumes are identified by role only,
/// never by absolute path.
/// </summary>
public sealed record StoragePreviewResult(
    IReadOnlyList<StorageVolumeResult> Volumes,
    IReadOnlyList<PlannedEvictionResult> PlannedMoves,
    IReadOnlyList<PlannedEvictionResult> PlannedDeletes,
    bool RecordingBelowFloor);

public sealed record StorageVolumeResult(
    string Role,
    long UsedBytes,
    long FreeBytes,
    long CapBytes,
    bool IsOnline,
    int MatchCount);

public sealed record PlannedEvictionResult(long MatchId, long SizeBytes);
