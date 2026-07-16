using System.Text.Json;
using BgRecorder.Core;
using BgRecorder.Core.Data;
using BgRecorder.Core.Events;
using BgRecorder.Core.Rating;
using BgRecorder.Core.Session;
using BgRecorder.Core.Storage;
using BgRecorder.Ui;
using Xunit;

namespace BgRecorder.Ui.Tests;

public sealed class UiBridgeTests
{
    [Fact]
    public async Task Library_list_returns_ui_fields_and_never_exposes_the_file_path()
    {
        var videoPath = Path.GetTempFileName();
        try
        {
            var match = SampleMatch(videoPath);
            var repository = new FakeRepository(match);
            var bridge = NewBridge(repository, new FakeCoordinator { State = CoordinatorState.Armed });

            var json = await bridge.HandleRequestAsync(Request("1", "library.list"));
            using var document = JsonDocument.Parse(json);
            var result = document.RootElement.GetProperty("result");
            var row = result.GetProperty("matches")[0];

            Assert.Equal("armed", result.GetProperty("coordinatorState").GetString());
            Assert.Equal(42, row.GetProperty("id").GetInt64());
            Assert.Equal("solo", row.GetProperty("gameType").GetString());
            Assert.Equal("https://media.bgrecorder.local/matches/42", row.GetProperty("mediaUrl").GetString());
            Assert.DoesNotContain(videoPath, json, StringComparison.OrdinalIgnoreCase);
            Assert.True(bridge.TryResolveVideoPath(42, out var resolved));
            Assert.Equal(Path.GetFullPath(videoPath), resolved);
        }
        finally
        {
            File.Delete(videoPath);
        }
    }

    [Fact]
    public async Task Library_get_returns_persisted_markers_in_the_typed_shape()
    {
        var match = SampleMatch(videoPath: null);
        var repository = new FakeRepository(
            match,
            [new MarkerRecord(match.Id, MarkerKind.CombatStart, 75_000, 2)]);
        var bridge = NewBridge(repository);

        var json = await bridge.HandleRequestAsync(Request(
            "detail",
            "library.get",
            new { matchId = 42 }));
        using var document = JsonDocument.Parse(json);
        var marker = document.RootElement.GetProperty("result").GetProperty("markers")[0];

        Assert.Equal("combatStart", marker.GetProperty("kind").GetString());
        Assert.Equal(75_000, marker.GetProperty("atMs").GetInt64());
        Assert.Equal(2, marker.GetProperty("tavernTurn").GetInt32());
    }

    [Fact]
    public async Task Set_starred_mutates_only_through_the_repository_contract()
    {
        var repository = new FakeRepository(SampleMatch(videoPath: null));
        var bridge = NewBridge(repository);

        var json = await bridge.HandleRequestAsync(Request(
            "star",
            "library.setStarred",
            new { matchId = 42, starred = true }));
        using var document = JsonDocument.Parse(json);

        Assert.NotNull(repository.LastStarUpdate);
        Assert.Equal((42L, true), repository.LastStarUpdate.Value);
        Assert.True(document.RootElement.GetProperty("result").GetProperty("starred").GetBoolean());
    }

    [Fact]
    public async Task Set_manual_rating_persists_through_the_repository_contract()
    {
        var repository = new FakeRepository(SampleMatch(videoPath: null));
        var bridge = NewBridge(repository);

        var json = await bridge.HandleRequestAsync(Request(
            "rate",
            "library.setManualRating",
            new { matchId = 42, rating = 4200 }));
        using var document = JsonDocument.Parse(json);

        Assert.NotNull(repository.LastManualRatingUpdate);
        Assert.Equal((42L, (int?)4200), repository.LastManualRatingUpdate.Value);
        Assert.Equal(4200, document.RootElement.GetProperty("result").GetProperty("rating").GetInt32());
    }

    [Fact]
    public async Task Set_manual_rating_to_null_clears_it()
    {
        var repository = new FakeRepository(SampleMatch(videoPath: null) with { ManualRating = 5000 });
        var bridge = NewBridge(repository);

        var json = await bridge.HandleRequestAsync(Request(
            "rate",
            "library.setManualRating",
            new { matchId = 42, rating = (int?)null }));
        using var document = JsonDocument.Parse(json);

        Assert.NotNull(repository.LastManualRatingUpdate);
        Assert.Equal((42L, (int?)null), repository.LastManualRatingUpdate.Value);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("result").GetProperty("rating").ValueKind);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(100_001)]
    public async Task Set_manual_rating_rejects_out_of_range_values(int rating)
    {
        var repository = new FakeRepository(SampleMatch(videoPath: null));
        var bridge = NewBridge(repository);

        var json = await bridge.HandleRequestAsync(Request(
            "rate",
            "library.setManualRating",
            new { matchId = 42, rating }));

        Assert.Contains("\"code\":-32602", json);
        Assert.Null(repository.LastManualRatingUpdate);
    }

    [Fact]
    public async Task Rating_get_projects_the_null_provider_as_disabled()
    {
        var bridge = NewBridge(new FakeRepository(SampleMatch(null)));

        var json = await bridge.HandleRequestAsync(Request("rating", "rating.get", new { mode = "solo" }));
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement.GetProperty("result");

        Assert.Equal("disabled", result.GetProperty("health").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("rating").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("sampledAt").ValueKind);
    }

    [Fact]
    public async Task Rating_get_rejects_an_unknown_mode()
    {
        var bridge = NewBridge(new FakeRepository(SampleMatch(null)));

        var json = await bridge.HandleRequestAsync(Request("rating", "rating.get", new { mode = "ranked" }));

        Assert.Contains("\"code\":-32602", json);
    }

    [Fact]
    public async Task Recorder_commands_return_the_coordinator_state()
    {
        var coordinator = new FakeCoordinator { State = CoordinatorState.Recording };
        var bridge = NewBridge(new FakeRepository(SampleMatch(null)), coordinator);

        var stop = await bridge.HandleRequestAsync(Request("stop", "recorder.stop"));
        var pause = await bridge.HandleRequestAsync(Request("pause", "recorder.pause"));
        var resume = await bridge.HandleRequestAsync(Request("resume", "recorder.resume"));

        Assert.Contains("\"state\":\"armed\"", stop);
        Assert.Contains("\"state\":\"paused\"", pause);
        Assert.Contains("\"state\":\"armed\"", resume);
        Assert.Equal(1, coordinator.StopCalls);
    }

    [Fact]
    public async Task Invalid_requests_return_json_rpc_errors_without_native_details()
    {
        var bridge = NewBridge(new FakeRepository(SampleMatch(null)));

        var malformed = await bridge.HandleRequestAsync("not json");
        var unknown = await bridge.HandleRequestAsync(Request("x", "library.nope"));

        Assert.Contains("\"code\":-32700", malformed);
        Assert.Contains("\"code\":-32601", unknown);
        Assert.DoesNotContain("System.", malformed);
        Assert.DoesNotContain("System.", unknown);
    }

    [Fact]
    public void State_notification_uses_the_frontend_enum_contract()
    {
        var json = UiBridge.CreateStateNotification(CoordinatorState.Finalizing);
        using var document = JsonDocument.Parse(json);

        Assert.Equal("recorder.stateChanged", document.RootElement.GetProperty("method").GetString());
        Assert.Equal("finalizing", document.RootElement.GetProperty("params").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Settings_get_projects_the_current_settings()
    {
        var settings = new FakeSettingsService(new AppSettings
        {
            Fps = 30,
            BitrateMbps = 20,
            GameOnlyAudio = false,
            MixMicrophone = true,
        });
        var bridge = NewBridge(new FakeRepository(SampleMatch(null)), settings: settings);

        var json = await bridge.HandleRequestAsync(Request("settings", "settings.get"));
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement.GetProperty("result");

        Assert.Equal(30, result.GetProperty("fps").GetInt32());
        Assert.Equal(20, result.GetProperty("bitrateMbps").GetInt32());
        Assert.False(result.GetProperty("gameOnlyAudio").GetBoolean());
        Assert.True(result.GetProperty("mixMicrophone").GetBoolean());
        Assert.False(string.IsNullOrEmpty(result.GetProperty("libraryDir").GetString()));
    }

    [Fact]
    public async Task Settings_set_persists_editable_fields_through_the_service()
    {
        var settings = new FakeSettingsService();
        var bridge = NewBridge(new FakeRepository(SampleMatch(null)), settings: settings);

        var json = await bridge.HandleRequestAsync(Request(
            "settings",
            "settings.set",
            new { fps = 30, bitrateMbps = 24, gameOnlyAudio = false, mixMicrophone = true }));
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement.GetProperty("result");

        Assert.NotNull(settings.LastSaved);
        Assert.Equal(30, settings.LastSaved!.Fps);
        Assert.Equal(24, settings.LastSaved.BitrateMbps);
        Assert.False(settings.LastSaved.GameOnlyAudio);
        Assert.True(settings.LastSaved.MixMicrophone);
        Assert.Equal(30, result.GetProperty("fps").GetInt32());
    }

    [Theory]
    [InlineData(10)]  // below the 15 fps floor
    [InlineData(300)] // above the 240 fps ceiling
    public async Task Settings_set_rejects_out_of_range_fps(int fps)
    {
        var settings = new FakeSettingsService();
        var bridge = NewBridge(new FakeRepository(SampleMatch(null)), settings: settings);

        var json = await bridge.HandleRequestAsync(Request(
            "settings",
            "settings.set",
            new { fps, bitrateMbps = 12, gameOnlyAudio = true, mixMicrophone = false }));

        Assert.Contains("\"code\":-32602", json);
        Assert.Null(settings.LastSaved);
    }

    [Fact]
    public async Task Settings_set_rejects_a_non_positive_bitrate()
    {
        var settings = new FakeSettingsService();
        var bridge = NewBridge(new FakeRepository(SampleMatch(null)), settings: settings);

        var json = await bridge.HandleRequestAsync(Request(
            "settings",
            "settings.set",
            new { fps = 60, bitrateMbps = 0, gameOnlyAudio = true, mixMicrophone = false }));

        Assert.Contains("\"code\":-32602", json);
        Assert.Null(settings.LastSaved);
    }

    [Fact]
    public async Task Delete_removes_the_row_and_the_video_file()
    {
        var videoPath = Path.GetTempFileName();
        try
        {
            var repository = new FakeRepository(SampleMatch(videoPath));
            var bridge = NewBridge(repository);

            var json = await bridge.HandleRequestAsync(Request("del", "library.delete", new { matchId = 42 }));
            using var document = JsonDocument.Parse(json);

            Assert.Equal(42, document.RootElement.GetProperty("result").GetProperty("matchId").GetInt64());
            Assert.Equal(42L, repository.DeletedId);
            Assert.False(File.Exists(videoPath)); // the on-disk video was removed, not just the row
        }
        finally
        {
            if (File.Exists(videoPath)) File.Delete(videoPath);
        }
    }

    [Fact]
    public async Task Delete_of_a_missing_match_returns_not_found_and_touches_nothing()
    {
        var repository = new FakeRepository(SampleMatch(null));
        var bridge = NewBridge(repository);

        var json = await bridge.HandleRequestAsync(Request("del", "library.delete", new { matchId = 999 }));

        Assert.Contains("\"code\":-32004", json);
        Assert.Null(repository.DeletedId);
    }

    [Fact]
    public async Task Delete_is_refused_when_the_video_file_cannot_be_removed()
    {
        // A non-Missing match whose file lives under an unreachable directory (a stand-in for an
        // unplugged archive drive: File.Delete throws DirectoryNotFoundException). The row must NOT be
        // deleted, or the multi-GB file would be orphaned where retention can never reclaim it.
        var unreachable = Path.Combine(Path.GetTempPath(), $"bgrec-gone-{Guid.NewGuid():N}", "video.mp4");
        var repository = new FakeRepository(SampleMatch(unreachable));
        var bridge = NewBridge(repository);

        var json = await bridge.HandleRequestAsync(Request("del", "library.delete", new { matchId = 42 }));

        Assert.Contains("\"code\":-32010", json);
        Assert.Null(repository.DeletedId); // row preserved so the file is not orphaned
    }

    [Fact]
    public async Task Library_list_flags_a_recording_whose_drive_is_offline()
    {
        // VideoStatus is Complete but the file does not exist → its drive is unplugged (offline), which
        // is distinct from a permanently Missing recording.
        var repository = new FakeRepository(SampleMatch(@"Z:\unplugged\gone.mp4"));
        var bridge = NewBridge(repository);

        var json = await bridge.HandleRequestAsync(Request("1", "library.list"));
        using var document = JsonDocument.Parse(json);
        var row = document.RootElement.GetProperty("result").GetProperty("matches")[0];

        Assert.True(row.GetProperty("isOffline").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("mediaUrl").ValueKind);
    }

    [Fact]
    public async Task Library_list_emits_a_thumbnail_route_and_never_exposes_the_thumbnail_path()
    {
        var thumbnailPath = Path.GetTempFileName();
        try
        {
            var match = SampleMatch(videoPath: null) with { ThumbnailPath = thumbnailPath };
            var bridge = NewBridge(new FakeRepository(match));

            var json = await bridge.HandleRequestAsync(Request("1", "library.list"));
            using var document = JsonDocument.Parse(json);
            var row = document.RootElement.GetProperty("result").GetProperty("matches")[0];

            Assert.Equal("https://media.bgrecorder.local/thumbnails/42", row.GetProperty("thumbnailUrl").GetString());
            Assert.DoesNotContain(thumbnailPath, json, StringComparison.OrdinalIgnoreCase); // path never leaves native
            Assert.True(bridge.TryResolveThumbnailPath(42, out var resolved));
            Assert.Equal(Path.GetFullPath(thumbnailPath), resolved);
        }
        finally
        {
            File.Delete(thumbnailPath);
        }
    }

    [Fact]
    public async Task Storage_get_projects_the_current_retention_caps()
    {
        var settings = new FakeSettingsService(new AppSettings
        {
            Storage = new StorageOptions { RecordingCapBytes = 50L << 30, HotSetSize = 8, TotalCapBytes = 200L << 30 },
        });
        var bridge = NewBridge(new FakeRepository(SampleMatch(null)), settings: settings);

        var json = await bridge.HandleRequestAsync(Request("s", "storage.get"));
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement.GetProperty("result");

        Assert.Equal(50L << 30, result.GetProperty("recordingCapBytes").GetInt64());
        Assert.Equal(8, result.GetProperty("hotSetSize").GetInt32());
        Assert.Equal(200L << 30, result.GetProperty("totalCapBytes").GetInt64());
    }

    [Fact]
    public async Task Storage_set_persists_caps_through_the_service()
    {
        var settings = new FakeSettingsService();
        var bridge = NewBridge(new FakeRepository(SampleMatch(null)), settings: settings);

        var json = await bridge.HandleRequestAsync(Request("s", "storage.set", new
        {
            recordingCapBytes = 100L << 30,
            recordingReserveBytes = 5L << 30,
            hotSetSize = 6,
            totalCapBytes = (long?)null,
        }));
        using var document = JsonDocument.Parse(json);

        Assert.NotNull(settings.LastSaved);
        Assert.Equal(100L << 30, settings.LastSaved!.Storage.RecordingCapBytes);
        Assert.Equal(6, settings.LastSaved.Storage.HotSetSize);
        Assert.Null(settings.LastSaved.Storage.TotalCapBytes);
        Assert.Equal(100L << 30, document.RootElement.GetProperty("result").GetProperty("recordingCapBytes").GetInt64());
    }

    [Fact]
    public async Task Storage_set_rejects_a_non_positive_recording_cap()
    {
        var settings = new FakeSettingsService();
        var bridge = NewBridge(new FakeRepository(SampleMatch(null)), settings: settings);

        var json = await bridge.HandleRequestAsync(Request("s", "storage.set", new
        {
            recordingCapBytes = 0,
            recordingReserveBytes = 5L << 30,
            hotSetSize = 6,
            totalCapBytes = (long?)null,
        }));

        Assert.Contains("\"code\":-32602", json);
        Assert.Null(settings.LastSaved);
    }

    [Fact]
    public async Task Storage_preview_projects_the_planner_result()
    {
        var preview = new StoragePreview(
            [new VolumeUsage(VolumeRole.Recording, 15L << 30, 100L << 30, 200L << 30, true, 3)],
            [],
            [new PlannedEviction(7, 5L << 30)],
            RecordingBelowFloor: false);
        var bridge = NewBridge(new FakeRepository(SampleMatch(null)), storagePlanner: new FakeStoragePlanner(preview));

        var json = await bridge.HandleRequestAsync(Request("s", "storage.preview"));
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement.GetProperty("result");

        var volume = result.GetProperty("volumes")[0];
        Assert.Equal("recording", volume.GetProperty("role").GetString());
        Assert.Equal(15L << 30, volume.GetProperty("usedBytes").GetInt64());
        Assert.Equal(3, volume.GetProperty("matchCount").GetInt32());

        var delete = result.GetProperty("plannedDeletes")[0];
        Assert.Equal(7, delete.GetProperty("matchId").GetInt64());
        Assert.Equal(0, result.GetProperty("plannedMoves").GetArrayLength());
    }

    [Fact]
    public async Task Storage_preview_with_caps_previews_the_proposed_options_not_the_in_force_ones()
    {
        var baseline = new AppSettings
        {
            Storage = new StorageOptions
            {
                RecordingCapBytes = 200L << 30,
                ArchiveVolumes = [new ArchiveVolumeOptions { Directory = @"D:\archive", CapBytes = 500L << 30 }],
            },
        };
        var planner = new FakeStoragePlanner();
        var bridge = NewBridge(
            new FakeRepository(SampleMatch(null)),
            settings: new FakeSettingsService(baseline),
            storagePlanner: planner);

        var json = await bridge.HandleRequestAsync(Request("s", "storage.preview", new
        {
            recordingCapBytes = 50L << 30,
            recordingReserveBytes = 2L << 30,
            hotSetSize = 3,
            totalCapBytes = (long?)null,
        }));

        Assert.Contains("\"result\"", json);
        Assert.Equal(0, planner.InForceCalls); // the hypothetical path, not the in-force one
        Assert.NotNull(planner.LastProposed);
        Assert.Equal(50L << 30, planner.LastProposed!.RecordingCapBytes);
        Assert.Equal(2L << 30, planner.LastProposed.RecordingReserveBytes);
        Assert.Equal(3, planner.LastProposed.HotSetSize);
        Assert.Null(planner.LastProposed.TotalCapBytes);
        // Archive drives are not editable caps: the proposal keeps the saved ones.
        Assert.Equal(@"D:\archive", Assert.Single(planner.LastProposed.ArchiveVolumes).Directory);
    }

    [Fact]
    public async Task Storage_preview_with_invalid_caps_is_rejected_without_touching_the_planner()
    {
        var planner = new FakeStoragePlanner();
        var bridge = NewBridge(new FakeRepository(SampleMatch(null)), storagePlanner: planner);

        var json = await bridge.HandleRequestAsync(Request("s", "storage.preview", new
        {
            recordingCapBytes = 0, // below the 1-byte minimum storage.set enforces
            recordingReserveBytes = 0,
            hotSetSize = 0,
            totalCapBytes = (long?)null,
        }));

        Assert.Contains("\"code\":-32602", json);
        Assert.Null(planner.LastProposed);
        Assert.Equal(0, planner.InForceCalls);
    }

    private static UiBridge NewBridge(
        IMatchRepository repository,
        ISessionCoordinator? coordinator = null,
        IRatingProvider? ratingProvider = null,
        ISettingsService? settings = null,
        IStoragePlanner? storagePlanner = null)
        => new(
            repository,
            coordinator ?? new FakeCoordinator(),
            ratingProvider ?? new NullRatingProvider(),
            settings ?? new FakeSettingsService(),
            storagePlanner ?? new FakeStoragePlanner());

    private static string Request(string id, string method, object? parameters = null)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        });

    private static MatchRecord SampleMatch(string? videoPath) => new()
    {
        Id = 42,
        StartedAt = new DateTimeOffset(2026, 7, 14, 20, 0, 0, TimeSpan.FromHours(-4)),
        GameType = BgGameType.Solo,
        HeroCardId = "BG_HERO_100",
        Place = 2,
        TavernTurns = 12,
        VideoStatus = videoPath is null ? VideoStatus.Missing : VideoStatus.Complete,
        VideoPath = videoPath,
        VideoSizeBytes = videoPath is null ? null : 4,
        VideoDuration = TimeSpan.FromMinutes(31),
        ManualRating = 8_100,
    };

    private sealed class FakeRepository : IMatchRepository
    {
        private MatchRecord _match;
        private readonly IReadOnlyList<MarkerRecord> _markers;

        public FakeRepository(MatchRecord match, IReadOnlyList<MarkerRecord>? markers = null)
        {
            _match = match;
            _markers = markers ?? [];
        }

        public (long MatchId, bool Starred)? LastStarUpdate { get; private set; }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<long> InsertMatchAsync(
            MatchRecord match,
            IReadOnlyList<MarkerRecord> markers,
            CancellationToken ct = default) => Task.FromResult(match.Id);

        public Task<bool> MatchExistsBySessionAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task UpdateVideoStatusAsync(long matchId, VideoStatus status, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<MatchRecord>> ListMatchesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MatchRecord>>([_match]);

        public Task<MatchDetailRecord?> GetMatchAsync(long matchId, CancellationToken ct = default)
            => Task.FromResult(matchId == _match.Id ? new MatchDetailRecord(_match, _markers) : null);

        public Task UpdateStarredAsync(long matchId, bool starred, CancellationToken ct = default)
        {
            LastStarUpdate = (matchId, starred);
            if (matchId == _match.Id)
            {
                _match = _match with { Starred = starred };
            }

            return Task.CompletedTask;
        }

        public Task UpdateVideoLocationAsync(long matchId, string videoPath, CancellationToken ct = default)
        {
            if (matchId == _match.Id)
            {
                _match = _match with { VideoPath = videoPath };
            }

            return Task.CompletedTask;
        }

        public long? DeletedId { get; private set; }

        public Task DeleteMatchAsync(long matchId, CancellationToken ct = default)
        {
            DeletedId = matchId;
            return Task.CompletedTask;
        }

        public (long MatchId, int? Rating)? LastManualRatingUpdate { get; private set; }

        public Task UpdateManualRatingAsync(long matchId, int? rating, CancellationToken ct = default)
        {
            LastManualRatingUpdate = (matchId, rating);
            if (matchId == _match.Id)
            {
                _match = _match with { ManualRating = rating };
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeCoordinator : ISessionCoordinator
    {
        public CoordinatorState State { get; set; } = CoordinatorState.Armed;
        public int StopCalls { get; private set; }
        public event Action<CoordinatorState>? StateChanged;
        public event Action<string>? Diagnostic
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopCurrentRecordingAsync()
        {
            StopCalls++;
            State = CoordinatorState.Armed;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public void PauseAutoRecording()
        {
            State = CoordinatorState.Paused;
            StateChanged?.Invoke(State);
        }

        public void ResumeNow()
        {
            State = CoordinatorState.Armed;
            StateChanged?.Invoke(State);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public FakeSettingsService(AppSettings? initial = null) => Current = initial ?? new AppSettings();

        public AppSettings Current { get; private set; }

        public AppSettings? LastSaved { get; private set; }

        public Task<AppSettings> UpdateAsync(AppSettings settings, CancellationToken ct = default)
        {
            LastSaved = settings;
            Current = settings;
            return Task.FromResult(settings);
        }
    }

    private sealed class FakeStoragePlanner : IStoragePlanner
    {
        private readonly StoragePreview _preview;

        public FakeStoragePlanner(StoragePreview? preview = null)
            => _preview = preview ?? new StoragePreview([], [], [], false);

        public StorageOptions? LastProposed { get; private set; }

        public int InForceCalls { get; private set; }

        public Task<StoragePreview> PreviewAsync(CancellationToken ct = default)
        {
            InForceCalls++;
            return Task.FromResult(_preview);
        }

        public Task<StoragePreview> PreviewAsync(StorageOptions proposed, CancellationToken ct = default)
        {
            LastProposed = proposed;
            return Task.FromResult(_preview);
        }
    }
}
