using System.Collections.Concurrent;
using System.Text.Json;
using BgRecorder.Core;
using BgRecorder.Core.Data;
using BgRecorder.Core.Events;
using BgRecorder.Core.Rating;
using BgRecorder.Core.Session;
using BgRecorder.Core.Storage;

namespace BgRecorder.Ui;

/// <summary>
/// Typed JSON-RPC boundary between the untrusted WebView document and native application services.
/// Only opaque match ids cross into media URLs; absolute recording paths remain native-only.
/// </summary>
public sealed class UiBridge
{
    private const string JsonRpcVersion = "2.0";
    private const string MediaOrigin = "https://media.bgrecorder.local";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IMatchRepository _repository;
    private readonly ISessionCoordinator _coordinator;
    private readonly IRatingProvider _ratingProvider;
    private readonly ISettingsService _settings;
    private readonly IStoragePlanner _storagePlanner;
    private readonly ConcurrentDictionary<long, string> _videoPaths = new();
    private readonly ConcurrentDictionary<long, string> _thumbnailPaths = new();

    public UiBridge(
        IMatchRepository repository,
        ISessionCoordinator coordinator,
        IRatingProvider ratingProvider,
        ISettingsService settings,
        IStoragePlanner storagePlanner)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _ratingProvider = ratingProvider ?? throw new ArgumentNullException(nameof(ratingProvider));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _storagePlanner = storagePlanner ?? throw new ArgumentNullException(nameof(storagePlanner));
    }

    public event Action<string>? Diagnostic;

    /// <summary>
    /// Handles one JSON-RPC 2.0 request. The SPA always sends string ids, making request/response
    /// correlation deterministic and avoiding lossy JavaScript-number conversion for large ids.
    /// </summary>
    public async Task<string> HandleRequestAsync(string message, CancellationToken ct = default)
    {
        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw RpcFault.InvalidRequest("The JSON-RPC payload must be an object.");
            }

            if (!root.TryGetProperty("jsonrpc", out var version) ||
                version.ValueKind != JsonValueKind.String ||
                version.GetString() != JsonRpcVersion)
            {
                throw RpcFault.InvalidRequest("jsonrpc must be '2.0'.");
            }

            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                throw RpcFault.InvalidRequest("A string request id is required.");
            }

            id = idElement.GetString();
            if (string.IsNullOrEmpty(id))
            {
                throw RpcFault.InvalidRequest("The request id cannot be empty.");
            }

            if (!root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(methodElement.GetString()))
            {
                throw RpcFault.InvalidRequest("A method name is required.");
            }

            var parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement.Clone()
                : default;
            var result = await DispatchAsync(methodElement.GetString()!, parameters, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new RpcSuccess(JsonRpcVersion, id, result), JsonOptions);
        }
        catch (JsonException ex)
        {
            Diagnostic?.Invoke($"UI bridge rejected malformed JSON: {ex.Message}");
            return SerializeError(id, -32700, "Parse error");
        }
        catch (RpcFault ex)
        {
            Diagnostic?.Invoke($"UI bridge request rejected: {ex.Message}");
            return SerializeError(id, ex.Code, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return SerializeError(id, -32800, "Request cancelled");
        }
        catch (Exception ex)
        {
            // Do not leak paths, stack traces, or SQLite details into WebView content.
            Diagnostic?.Invoke($"UI bridge request failed: {ex}");
            return SerializeError(id, -32603, "Internal error");
        }
    }

    /// <summary>A native-to-SPA JSON-RPC notification for live coordinator state changes.</summary>
    public static string CreateStateNotification(CoordinatorState state)
        => JsonSerializer.Serialize(
            new RpcNotification(
                JsonRpcVersion,
                "recorder.stateChanged",
                new RecorderStateResult(MapState(state))),
            JsonOptions);

    /// <summary>
    /// Resolves an opaque media route from the most recently trusted repository result. JavaScript
    /// can choose an id, but can never provide or escape to an arbitrary filesystem path.
    /// </summary>
    public bool TryResolveVideoPath(long matchId, out string path)
    {
        if (_videoPaths.TryGetValue(matchId, out var candidate) && File.Exists(candidate))
        {
            path = candidate;
            return true;
        }

        path = string.Empty;
        return false;
    }

    /// <summary>Resolves an opaque thumbnail route the same way as the media route — id in, trusted path out.</summary>
    public bool TryResolveThumbnailPath(long matchId, out string path)
    {
        if (_thumbnailPaths.TryGetValue(matchId, out var candidate) && File.Exists(candidate))
        {
            path = candidate;
            return true;
        }

        path = string.Empty;
        return false;
    }

    private async Task<object> DispatchAsync(string method, JsonElement parameters, CancellationToken ct)
        => method switch
        {
            "library.list" => await ListLibraryAsync(ct).ConfigureAwait(false),
            "library.get" => await GetMatchAsync(RequiredInt64(parameters, "matchId"), ct).ConfigureAwait(false),
            "library.setStarred" => await SetStarredAsync(
                    RequiredInt64(parameters, "matchId"),
                    RequiredBoolean(parameters, "starred"),
                    ct)
                .ConfigureAwait(false),
            "library.setManualRating" => await SetManualRatingAsync(
                    RequiredInt64(parameters, "matchId"),
                    RequiredNullableRating(parameters, "rating"),
                    ct)
                .ConfigureAwait(false),
            "library.delete" => await DeleteMatchAsync(RequiredInt64(parameters, "matchId"), ct).ConfigureAwait(false),
            "rating.get" => await GetRatingAsync(RequiredMode(parameters, "mode"), ct).ConfigureAwait(false),
            "settings.get" => GetSettings(),
            "settings.set" => await SetSettingsAsync(parameters, ct).ConfigureAwait(false),
            "storage.preview" => await GetStoragePreviewAsync(ct).ConfigureAwait(false),
            "storage.get" => GetStorageSettings(),
            "storage.set" => await SetStorageSettingsAsync(parameters, ct).ConfigureAwait(false),
            "recorder.stop" => await StopRecordingAsync().ConfigureAwait(false),
            "recorder.pause" => PauseRecording(),
            "recorder.resume" => ResumeRecording(),
            _ => throw new RpcFault(-32601, $"Method not found: {method}"),
        };

    private async Task<LibrarySnapshot> ListLibraryAsync(CancellationToken ct)
    {
        var matches = await _repository.ListMatchesAsync(ct).ConfigureAwait(false);
        var summaries = matches.Select(MapSummary).ToList();
        return new LibrarySnapshot(MapState(_coordinator.State), summaries);
    }

    private async Task<LibraryMatchDetail> GetMatchAsync(long matchId, CancellationToken ct)
    {
        var detail = await _repository.GetMatchAsync(matchId, ct).ConfigureAwait(false)
            ?? throw new RpcFault(-32004, $"Match {matchId} was not found.");

        return new LibraryMatchDetail(
            MapSummary(detail.Match),
            detail.Markers.Select(m => new LibraryMarker(MapMarkerKind(m.Kind), m.AtMs, m.TavernTurn)).ToList());
    }

    private async Task<StarredResult> SetStarredAsync(long matchId, bool starred, CancellationToken ct)
    {
        // Distinguish a stale UI row from a successful mutation that happens to affect zero rows.
        if (await _repository.GetMatchAsync(matchId, ct).ConfigureAwait(false) is null)
        {
            throw new RpcFault(-32004, $"Match {matchId} was not found.");
        }

        await _repository.UpdateStarredAsync(matchId, starred, ct).ConfigureAwait(false);
        return new StarredResult(matchId, starred);
    }

    private async Task<ManualRatingResult> SetManualRatingAsync(long matchId, int? rating, CancellationToken ct)
    {
        // Distinguish a stale UI row from a mutation that legitimately affects zero rows, mirroring
        // the starred path. Recording never depends on rating, so this is a pure metadata edit.
        if (await _repository.GetMatchAsync(matchId, ct).ConfigureAwait(false) is null)
        {
            throw new RpcFault(-32004, $"Match {matchId} was not found.");
        }

        await _repository.UpdateManualRatingAsync(matchId, rating, ct).ConfigureAwait(false);
        return new ManualRatingResult(matchId, rating);
    }

    /// <summary>
    /// Permanently removes a recording: its video file, then its library row (markers cascade). The file
    /// is resolved server-side from trusted repository state — the WebView only ever supplies the opaque
    /// match id, never a path. If the file cannot be removed and the row still expects one (its drive is
    /// disconnected, or the file is locked), the delete is REFUSED rather than dropping the row and
    /// orphaning a file the retention engine — which only enumerates rows — could never see or reclaim.
    /// </summary>
    private async Task<DeletedResult> DeleteMatchAsync(long matchId, CancellationToken ct)
    {
        var detail = await _repository.GetMatchAsync(matchId, ct).ConfigureAwait(false)
            ?? throw new RpcFault(-32004, $"Match {matchId} was not found.");

        // Attempt the video removal always (a Missing row whose file resurfaced still gets cleaned up),
        // but only refuse the row deletion when a still-expected file could not be removed — otherwise a
        // multi-GB file on a disconnected drive would be orphaned where retention can never reclaim it.
        var removed = TryRemoveVideoFile(matchId, detail.Match.VideoPath);
        if (!removed && detail.Match.VideoStatus != VideoStatus.Missing)
        {
            throw new RpcFault(-32010, "This recording's drive is unavailable; reconnect it, then delete again.");
        }

        // The thumbnail follows the video's fate; its removal is best-effort and never blocks the delete.
        TryDeleteThumbnailFile(matchId, detail.Match.ThumbnailPath);
        await _repository.DeleteMatchAsync(matchId, ct).ConfigureAwait(false);
        _videoPaths.TryRemove(matchId, out _);
        _thumbnailPaths.TryRemove(matchId, out _);
        return new DeletedResult(matchId);
    }

    /// <summary>
    /// Removes the video file. Returns true when the file is confirmably gone from a reachable location
    /// (deleted now, or already absent while its directory is reachable — <see cref="File.Delete(string)"/>
    /// no-ops on a missing file), and false when it could not be removed (its drive/directory is
    /// unreachable — a disconnected archive — or it is locked). A false return is the signal not to
    /// orphan the file by dropping its row.
    /// </summary>
    private bool TryRemoveVideoFile(long matchId, string? videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
        {
            return true; // nothing to remove
        }

        try
        {
            File.Delete(Path.GetFullPath(videoPath));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreachable drive (DirectoryNotFoundException derives from IOException) or a locked file:
            // keep the row so the file is not orphaned; the user retries once the drive/file is available.
            Diagnostic?.Invoke($"Could not delete the video file for match {matchId}: {ex.Message}");
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A malformed stored path can never point at a real file, so a stale row must stay deletable.
            Diagnostic?.Invoke($"Ignored invalid video path for match {matchId}: {ex.Message}");
            return true;
        }
    }

    /// <summary>Best-effort removal of the thumbnail sibling; its absence never blocks the delete.</summary>
    private void TryDeleteThumbnailFile(long matchId, string? thumbnailPath)
    {
        if (string.IsNullOrWhiteSpace(thumbnailPath))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(thumbnailPath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            Diagnostic?.Invoke($"Could not delete the thumbnail file for match {matchId}: {ex.Message}");
        }
    }

    private async Task<RatingInfoResult> GetRatingAsync(BgGameType mode, CancellationToken ct)
    {
        // v1's provider is the null one, so this reports Disabled with a null rating; the interface is
        // wired end to end so a post-v1 clean-room reader lights the same path up without UI changes.
        var snapshot = await _ratingProvider.TryGetAsync(mode, ct).ConfigureAwait(false);
        return new RatingInfoResult(MapHealth(_ratingProvider.Health), snapshot?.Rating, snapshot?.SampledAt);
    }

    private static string MapHealth(RatingHealth health) => health switch
    {
        RatingHealth.Ok => "ok",
        RatingHealth.AttachFailed => "attachFailed",
        RatingHealth.PatchBroken => "patchBroken",
        _ => "disabled",
    };

    private SettingsResult GetSettings() => MapSettings(_settings.Current);

    /// <summary>
    /// Persists the editable recording settings. Paths are read-only here (changing the library location
    /// needs migration, out of M6 scope). Because the coordinator captured the settings instance at
    /// startup, these apply to the NEXT launch — the UI states that rather than implying a live effect.
    /// </summary>
    private async Task<SettingsResult> SetSettingsAsync(JsonElement parameters, CancellationToken ct)
    {
        var next = _settings.Current with
        {
            Fps = RequiredInt32InRange(parameters, "fps", 15, 240),
            BitrateMbps = RequiredInt32InRange(parameters, "bitrateMbps", 1, 100),
            GameOnlyAudio = RequiredBoolean(parameters, "gameOnlyAudio"),
            MixMicrophone = RequiredBoolean(parameters, "mixMicrophone"),
        };

        var saved = await _settings.UpdateAsync(next, ct).ConfigureAwait(false);
        return MapSettings(saved);
    }

    private static SettingsResult MapSettings(AppSettings settings) => new(
        settings.HearthstoneInstallDir,
        settings.LibraryDir,
        settings.StagingDir,
        settings.Fps,
        settings.BitrateMbps,
        settings.GameOnlyAudio,
        settings.MixMicrophone);

    private async Task<StoragePreviewResult> GetStoragePreviewAsync(CancellationToken ct)
    {
        var preview = await _storagePlanner.PreviewAsync(ct).ConfigureAwait(false);
        return new StoragePreviewResult(
            preview.Volumes.Select(v => new StorageVolumeResult(
                MapVolumeRole(v.Role), v.UsedBytes, v.FreeBytes, v.CapBytes, v.IsOnline, v.MatchCount)).ToList(),
            preview.PlannedMoves.Select(m => new PlannedEvictionResult(m.MatchId, m.SizeBytes)).ToList(),
            preview.PlannedDeletes.Select(d => new PlannedEvictionResult(d.MatchId, d.SizeBytes)).ToList(),
            preview.RecordingBelowFloor);
    }

    private StorageSettingsResult GetStorageSettings() => MapStorage(_settings.Current.Storage);

    /// <summary>
    /// Persists the retention caps. Archive-drive management (adding/removing folders) is a separate,
    /// path-picking concern and stays read-only here. Like the recording settings, changes take effect on
    /// the next launch because the running storage engine captured its options at startup — the UI says so.
    /// </summary>
    private async Task<StorageSettingsResult> SetStorageSettingsAsync(JsonElement parameters, CancellationToken ct)
    {
        var current = _settings.Current;
        var next = current with
        {
            Storage = current.Storage with
            {
                RecordingCapBytes = RequiredInt64InRange(parameters, "recordingCapBytes", 1, long.MaxValue),
                RecordingReserveBytes = RequiredInt64InRange(parameters, "recordingReserveBytes", 0, long.MaxValue),
                HotSetSize = RequiredInt32InRange(parameters, "hotSetSize", 0, 1000),
                TotalCapBytes = RequiredNullablePositiveInt64(parameters, "totalCapBytes"),
            },
        };

        var saved = await _settings.UpdateAsync(next, ct).ConfigureAwait(false);
        return MapStorage(saved.Storage);
    }

    private static StorageSettingsResult MapStorage(StorageOptions storage) => new(
        storage.RecordingCapBytes,
        storage.RecordingReserveBytes,
        storage.HotSetSize,
        storage.TotalCapBytes,
        storage.ArchiveVolumes
            .Select(a => new ArchiveVolumeResult(a.Directory, a.CapBytes, a.ReserveBytes, a.Priority))
            .ToList());

    private static string MapVolumeRole(VolumeRole role) => role switch
    {
        VolumeRole.Recording => "recording",
        VolumeRole.Archive => "archive",
        _ => "recording",
    };

    private async Task<RecorderStateResult> StopRecordingAsync()
    {
        await _coordinator.StopCurrentRecordingAsync().ConfigureAwait(false);
        return new RecorderStateResult(MapState(_coordinator.State));
    }

    private RecorderStateResult PauseRecording()
    {
        _coordinator.PauseAutoRecording();
        return new RecorderStateResult(MapState(_coordinator.State));
    }

    private RecorderStateResult ResumeRecording()
    {
        _coordinator.ResumeNow();
        return new RecorderStateResult(MapState(_coordinator.State));
    }

    private LibraryMatchSummary MapSummary(MatchRecord match)
    {
        string? mediaUrl = null;
        // "Offline" = the row expects a playable video but the file is not currently reachable (its
        // archive drive is unplugged), as distinct from VideoStatus.Missing (permanently gone). Derived
        // from the same File.Exists probe that gates the media URL, so it is always current — no stored
        // flag to go stale when a drive is plugged or pulled between writes.
        var isOffline = false;
        if (match.VideoStatus != VideoStatus.Missing && !string.IsNullOrWhiteSpace(match.VideoPath))
        {
            try
            {
                var fullPath = Path.GetFullPath(match.VideoPath);
                if (File.Exists(fullPath))
                {
                    _videoPaths[match.Id] = fullPath;
                    mediaUrl = $"{MediaOrigin}/matches/{match.Id}";
                }
                else
                {
                    _videoPaths.TryRemove(match.Id, out _);
                    isOffline = true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                _videoPaths.TryRemove(match.Id, out _);
                Diagnostic?.Invoke($"Ignored invalid video path for match {match.Id}: {ex.Message}");
            }
        }
        else
        {
            _videoPaths.TryRemove(match.Id, out _);
        }

        return new LibraryMatchSummary(
            match.Id,
            match.StartedAt,
            match.GameType switch
            {
                BgGameType.Solo => "solo",
                BgGameType.Duos => "duos",
                _ => "notBattlegrounds",
            },
            match.HeroCardId,
            match.Place,
            match.TavernTurns,
            match.VideoStatus switch
            {
                VideoStatus.Complete => "complete",
                VideoStatus.Incomplete => "incomplete",
                VideoStatus.Missing => "missing",
                _ => "missing",
            },
            match.VideoSizeBytes,
            match.VideoDuration is { } duration ? (long)duration.TotalMilliseconds : null,
            match.Starred,
            match.ManualRating,
            mediaUrl,
            isOffline,
            MapThumbnailUrl(match));
    }

    /// <summary>
    /// Caches the trusted thumbnail path and returns an opaque route, or null when there is no readable
    /// thumbnail. Mirrors the media-URL boundary: the WebView only ever gets the id, never the path.
    /// </summary>
    private string? MapThumbnailUrl(MatchRecord match)
    {
        if (!string.IsNullOrWhiteSpace(match.ThumbnailPath))
        {
            try
            {
                var fullPath = Path.GetFullPath(match.ThumbnailPath);
                if (File.Exists(fullPath))
                {
                    _thumbnailPaths[match.Id] = fullPath;
                    return $"{MediaOrigin}/thumbnails/{match.Id}";
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Diagnostic?.Invoke($"Ignored invalid thumbnail path for match {match.Id}: {ex.Message}");
            }
        }

        _thumbnailPaths.TryRemove(match.Id, out _);
        return null;
    }

    private static string MapMarkerKind(MarkerKind kind) => kind switch
    {
        MarkerKind.CombatStart => "combatStart",
        MarkerKind.TurnStart => "turnStart",
        MarkerKind.MatchEnd => "matchEnd",
        _ => "unknown",
    };

    private static string MapState(CoordinatorState state) => state switch
    {
        CoordinatorState.GameNotFound => "gameNotFound",
        CoordinatorState.Armed => "armed",
        CoordinatorState.Recording => "recording",
        CoordinatorState.Finalizing => "finalizing",
        CoordinatorState.Paused => "paused",
        CoordinatorState.StorageBlocked => "storageBlocked",
        _ => "gameNotFound",
    };

    private static long RequiredInt64(JsonElement parameters, string propertyName)
    {
        var value = RequiredProperty(parameters, propertyName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var parsed) || parsed <= 0)
        {
            throw RpcFault.InvalidParams($"{propertyName} must be a positive integer.");
        }

        return parsed;
    }

    /// <summary>
    /// A rating parameter that must be present but may be JSON null (clear the rating) or a
    /// non-negative integer within a sane bound. Rejects fractional, negative, or absurd values.
    /// </summary>
    private static int? RequiredNullableRating(JsonElement parameters, string propertyName)
    {
        var value = RequiredProperty(parameters, propertyName);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var parsed) ||
            parsed < 0 ||
            parsed > 100_000)
        {
            throw RpcFault.InvalidParams($"{propertyName} must be null or an integer between 0 and 100000.");
        }

        return parsed;
    }

    private static BgGameType RequiredMode(JsonElement parameters, string propertyName)
    {
        var value = RequiredProperty(parameters, propertyName);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() switch
            {
                "solo" => BgGameType.Solo,
                "duos" => BgGameType.Duos,
                _ => throw RpcFault.InvalidParams($"{propertyName} must be 'solo' or 'duos'."),
            }
            : throw RpcFault.InvalidParams($"{propertyName} must be 'solo' or 'duos'.");
    }

    private static bool RequiredBoolean(JsonElement parameters, string propertyName)
    {
        var value = RequiredProperty(parameters, propertyName);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw RpcFault.InvalidParams($"{propertyName} must be a boolean.");
        }

        return value.GetBoolean();
    }

    private static int RequiredInt32InRange(JsonElement parameters, string propertyName, int min, int max)
    {
        var value = RequiredProperty(parameters, propertyName);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var parsed) ||
            parsed < min ||
            parsed > max)
        {
            throw RpcFault.InvalidParams($"{propertyName} must be an integer between {min} and {max}.");
        }

        return parsed;
    }

    private static long RequiredInt64InRange(JsonElement parameters, string propertyName, long min, long max)
    {
        var value = RequiredProperty(parameters, propertyName);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var parsed) ||
            parsed < min ||
            parsed > max)
        {
            throw RpcFault.InvalidParams($"{propertyName} must be an integer between {min} and {max}.");
        }

        return parsed;
    }

    /// <summary>A cap that must be present but may be JSON null (no cap) or a strictly positive integer.</summary>
    private static long? RequiredNullablePositiveInt64(JsonElement parameters, string propertyName)
    {
        var value = RequiredProperty(parameters, propertyName);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var parsed) || parsed < 1)
        {
            throw RpcFault.InvalidParams($"{propertyName} must be null or a positive integer.");
        }

        return parsed;
    }

    private static JsonElement RequiredProperty(JsonElement parameters, string propertyName)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty(propertyName, out var value))
        {
            throw RpcFault.InvalidParams($"Missing parameter: {propertyName}.");
        }

        return value;
    }

    private static string SerializeError(string? id, int code, string message)
        => JsonSerializer.Serialize(
            new RpcErrorResponse(JsonRpcVersion, id, new RpcError(code, message)),
            JsonOptions);

    private sealed record RpcSuccess(string Jsonrpc, string Id, object Result);
    private sealed record RpcErrorResponse(string Jsonrpc, string? Id, RpcError Error);
    private sealed record RpcError(int Code, string Message);
    private sealed record RpcNotification(string Jsonrpc, string Method, object Params);

    private sealed class RpcFault(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;

        public static RpcFault InvalidRequest(string message) => new(-32600, message);
        public static RpcFault InvalidParams(string message) => new(-32602, message);
    }
}
