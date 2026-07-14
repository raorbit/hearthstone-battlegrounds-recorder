using BgRecorder.Core.Data;
using BgRecorder.Core.Events;
using Xunit;

namespace BgRecorder.Data.Tests;

public sealed class MatchAssemblerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    private static RecordingTimeline Timeline(DateTimeOffset firstFrame) =>
        new(firstFrame, @"C:\vods\m.mp4", 999_000L, TimeSpan.FromMilliseconds(120_000));

    private readonly MatchAssembler _assembler = new();

    [Fact]
    public void Happy_path_maps_every_field_and_marker()
    {
        var timeline = Timeline(At(0));
        var events = new GameEvent[]
        {
            new MatchStarted(At(0)),
            new GameTypeResolved(At(0.5), BgGameType.Solo),
            new LocalHeroResolved(At(1), "HERO_A"),
            new TurnStarted(At(2), RawTurn: 1, TavernTurn: 1),
            new CombatStarted(At(30), TavernTurn: 1),
            new TurnStarted(At(60), RawTurn: 3, TavernTurn: 2),
            new CombatStarted(At(90), TavernTurn: 2),
            new PlacementChanged(At(100), 4),
            new PlacementChanged(At(110), 3),
            new MatchEnded(At(115), FinalPlace: 3, PlayState.Won, Truncated: false),
        };

        var (match, markers) = _assembler.Assemble(events, timeline, VideoStatus.Complete);

        Assert.Equal(BgGameType.Solo, match.GameType);
        Assert.Equal("HERO_A", match.HeroCardId);
        Assert.Equal(3, match.Place);
        Assert.Equal(2, match.TavernTurns);
        Assert.Equal(PlayState.Won, match.PlayState);
        Assert.False(match.Truncated);
        Assert.Equal(At(0), match.StartedAt);
        Assert.Equal(At(115), match.EndedAt);
        Assert.Equal(VideoStatus.Complete, match.VideoStatus);
        Assert.Equal(timeline.FinalVideoPath, match.VideoPath);
        Assert.Equal(timeline.SizeBytes, match.VideoSizeBytes);
        Assert.Equal(timeline.Duration, match.VideoDuration);

        Assert.Equal(5, markers.Count);
        Assert.All(markers, m => Assert.Equal(0, m.MatchId)); // id is assigned at insert time

        Assert.Equal(MarkerKind.TurnStart, markers[0].Kind);
        Assert.Equal(2_000, markers[0].AtMs);
        Assert.Equal(1, markers[0].TavernTurn);

        Assert.Equal(MarkerKind.CombatStart, markers[1].Kind);
        Assert.Equal(30_000, markers[1].AtMs);
        Assert.Equal(1, markers[1].TavernTurn);

        Assert.Equal(MarkerKind.TurnStart, markers[2].Kind);
        Assert.Equal(60_000, markers[2].AtMs);
        Assert.Equal(2, markers[2].TavernTurn);

        Assert.Equal(MarkerKind.CombatStart, markers[3].Kind);
        Assert.Equal(90_000, markers[3].AtMs);
        Assert.Equal(2, markers[3].TavernTurn);

        Assert.Equal(MarkerKind.MatchEnd, markers[4].Kind);
        Assert.Equal(115_000, markers[4].AtMs);
        Assert.Equal(2, markers[4].TavernTurn); // ended on the last turn seen
    }

    [Fact]
    public void Hero_swap_takes_the_last_resolved_hero()
    {
        var events = new GameEvent[]
        {
            new MatchStarted(At(0)),
            new LocalHeroResolved(At(1), "HERO_MULLIGAN"),
            new LocalHeroResolved(At(5), "HERO_FINAL"),
            new MatchEnded(At(10), 1, PlayState.Won, false),
        };

        var (match, _) = _assembler.Assemble(events, timeline: null, VideoStatus.Complete);

        Assert.Equal("HERO_FINAL", match.HeroCardId);
    }

    [Fact]
    public void Duos_game_type_is_resolved()
    {
        var events = new GameEvent[]
        {
            new MatchStarted(At(0)),
            new GameTypeResolved(At(1), BgGameType.Duos),
            new MatchEnded(At(10), 2, PlayState.Lost, false),
        };

        var (match, _) = _assembler.Assemble(events, timeline: null, VideoStatus.Complete);

        Assert.Equal(BgGameType.Duos, match.GameType);
    }

    [Fact]
    public void No_match_ended_marks_truncated_unknown_with_null_end()
    {
        var events = new GameEvent[]
        {
            new MatchStarted(At(0)),
            new GameTypeResolved(At(1), BgGameType.Solo),
            new LocalHeroResolved(At(1), "HERO_A"),
            new TurnStarted(At(2), 1, 1),
            new PlacementChanged(At(50), 5),
        };

        var (match, _) = _assembler.Assemble(events, timeline: null, VideoStatus.Incomplete);

        Assert.True(match.Truncated);
        Assert.Equal(PlayState.Unknown, match.PlayState);
        Assert.Null(match.EndedAt);
        Assert.Equal(At(0), match.StartedAt);
        Assert.Equal(5, match.Place); // falls back to last placement
        Assert.Equal(1, match.TavernTurns);
    }

    [Fact]
    public void No_timeline_yields_zero_markers_and_null_video_fields()
    {
        var events = new GameEvent[]
        {
            new MatchStarted(At(0)),
            new TurnStarted(At(2), 1, 1),
            new CombatStarted(At(30), 1),
            new MatchEnded(At(60), 4, PlayState.Conceded, false),
        };

        var (match, markers) = _assembler.Assemble(events, timeline: null, VideoStatus.Incomplete);

        Assert.Empty(markers);
        Assert.Null(match.VideoPath);
        Assert.Null(match.VideoSizeBytes);
        Assert.Null(match.VideoDuration);
        Assert.Equal(VideoStatus.Incomplete, match.VideoStatus);
    }

    [Fact]
    public void Events_before_first_frame_clamp_to_zero_ms()
    {
        // First frame lands at +50s; earlier events must clamp to 0, later ones stay positive.
        var timeline = Timeline(At(50));
        var events = new GameEvent[]
        {
            new MatchStarted(At(0)),
            new TurnStarted(At(10), 1, 1),   // before first frame -> 0
            new CombatStarted(At(40), 1),    // before first frame -> 0
            new TurnStarted(At(60), 3, 2),   // +10s -> 10_000
            new MatchEnded(At(70), 1, PlayState.Won, false), // +20s -> 20_000
        };

        var (_, markers) = _assembler.Assemble(events, timeline, VideoStatus.Complete);

        Assert.Equal(4, markers.Count);
        Assert.Equal(0, markers[0].AtMs);
        Assert.Equal(0, markers[1].AtMs);
        Assert.Equal(10_000, markers[2].AtMs);
        Assert.Equal(20_000, markers[3].AtMs);
    }

    [Fact]
    public void Placement_prefers_final_place_over_leaderboard()
    {
        var events = new GameEvent[]
        {
            new MatchStarted(At(0)),
            new PlacementChanged(At(50), 5),
            new MatchEnded(At(60), FinalPlace: 1, PlayState.Won, false),
        };

        var (match, _) = _assembler.Assemble(events, timeline: null, VideoStatus.Complete);

        Assert.Equal(1, match.Place);
    }

    [Fact]
    public void Placement_falls_back_to_last_leaderboard_when_final_place_absent()
    {
        var events = new GameEvent[]
        {
            new MatchStarted(At(0)),
            new PlacementChanged(At(40), 6),
            new PlacementChanged(At(50), 4),
            new MatchEnded(At(60), FinalPlace: null, PlayState.Lost, false),
        };

        var (match, _) = _assembler.Assemble(events, timeline: null, VideoStatus.Complete);

        Assert.Equal(4, match.Place);
    }

    [Fact]
    public void Placement_is_null_when_no_source_present()
    {
        var events = new GameEvent[]
        {
            new MatchStarted(At(0)),
            new MatchEnded(At(60), FinalPlace: null, PlayState.Conceded, false),
        };

        var (match, _) = _assembler.Assemble(events, timeline: null, VideoStatus.Complete);

        Assert.Null(match.Place);
        Assert.Equal(0, match.TavernTurns); // no TurnStarted events
    }
}
