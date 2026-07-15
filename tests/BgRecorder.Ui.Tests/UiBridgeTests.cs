using System.Text.Json;
using BgRecorder.Core.Data;
using BgRecorder.Core.Events;
using BgRecorder.Core.Rating;
using BgRecorder.Core.Session;
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
            var bridge = new UiBridge(repository, new FakeCoordinator { State = CoordinatorState.Armed }, new NullRatingProvider());

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
        var bridge = new UiBridge(repository, new FakeCoordinator(), new NullRatingProvider());

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
        var bridge = new UiBridge(repository, new FakeCoordinator(), new NullRatingProvider());

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
        var bridge = new UiBridge(repository, new FakeCoordinator(), new NullRatingProvider());

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
        var bridge = new UiBridge(repository, new FakeCoordinator(), new NullRatingProvider());

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
        var bridge = new UiBridge(repository, new FakeCoordinator(), new NullRatingProvider());

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
        var bridge = new UiBridge(new FakeRepository(SampleMatch(null)), new FakeCoordinator(), new NullRatingProvider());

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
        var bridge = new UiBridge(new FakeRepository(SampleMatch(null)), new FakeCoordinator(), new NullRatingProvider());

        var json = await bridge.HandleRequestAsync(Request("rating", "rating.get", new { mode = "ranked" }));

        Assert.Contains("\"code\":-32602", json);
    }

    [Fact]
    public async Task Recorder_commands_return_the_coordinator_state()
    {
        var coordinator = new FakeCoordinator { State = CoordinatorState.Recording };
        var bridge = new UiBridge(new FakeRepository(SampleMatch(null)), coordinator, new NullRatingProvider());

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
        var bridge = new UiBridge(new FakeRepository(SampleMatch(null)), new FakeCoordinator(), new NullRatingProvider());

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

        public Task DeleteMatchAsync(long matchId, CancellationToken ct = default) => Task.CompletedTask;

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
}
