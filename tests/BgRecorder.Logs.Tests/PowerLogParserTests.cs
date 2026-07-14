using BgRecorder.Core.Events;
using BgRecorder.Logs;
using Xunit;

namespace BgRecorder.Logs.Tests;

/// <summary>
/// Fixture-driven regression tests: the salvaged Spike A parser's verdicts over all 17 sanitized matches are
/// hardcoded in <see cref="Fixtures.Table"/>; the production <see cref="PowerLogParser"/> must reproduce them
/// exactly from its streamed event output. Sanitized-vs-raw structural equivalence was already established in
/// Spike A (the sanitizer only distills parser-irrelevant lines and redacts BattleTags/account ids, none of
/// which the parser reads), so these tests drive the committed sanitized corpus directly.
/// </summary>
public sealed class PowerLogParserTests
{
    public static IEnumerable<object[]> AllFixtures() =>
        Fixtures.Table.Select(e => new object[] { e.Index });

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Fixture_matches_spike_ground_truth(int index)
    {
        var expected = Fixtures.Table.Single(e => e.Index == index);
        var (parser, events) = Fixtures.DriveFixture(index);

        // Exactly one match opened and closed.
        Assert.Single(events.OfType<MatchStarted>());
        var end = Assert.Single(events.OfType<MatchEnded>());

        // Game type.
        var gameType = Assert.Single(events.OfType<GameTypeResolved>());
        Assert.Equal(expected.GameType, gameType.GameType);

        // Hero: last resolution at/before the first combat (mulligan-swap safe), else last overall.
        var firstCombat = events.OfType<CombatStarted>().FirstOrDefault();
        var heroes = events.OfType<LocalHeroResolved>().ToList();
        Assert.NotEmpty(heroes);
        var hero = firstCombat is null
            ? heroes[^1]
            : heroes.LastOrDefault(h => h.Timestamp <= firstCombat.Timestamp) ?? heroes[^1];
        Assert.Equal(expected.Hero, hero.HeroCardId);

        // Placement: the terminal value, cross-checked against the last PlacementChanged.
        Assert.Equal(expected.Place, end.FinalPlace);
        var lastPlacement = Assert.IsType<PlacementChanged>(events.OfType<PlacementChanged>().Last());
        Assert.Equal(expected.Place, lastPlacement.Place);

        // Tavern turns = max displayed turn.
        int tavernTurns = events.OfType<TurnStarted>().Max(t => t.TavernTurn);
        Assert.Equal(expected.TavernTurns, tavernTurns);

        // Combat count via events, and the parser's two independent counters must agree.
        int combatEvents = events.OfType<CombatStarted>().Count();
        Assert.Equal(expected.Combat, combatEvents);
        Assert.Equal(expected.Combat, parser.CurrentMatchCombatStarts);
        Assert.Equal(expected.Combat, parser.CurrentMatchBoardVisualState2);

        // End state.
        Assert.Equal(expected.Truncated, end.Truncated);
        Assert.Equal(expected.PlayState, end.PlayState);
    }

    [Fact]
    public void Combat_boundary_signal_agrees_with_board_visual_state_across_whole_corpus()
    {
        // The even-raw-TURN combat detector and the independent BOARD_VISUAL_STATE=2 counter must never
        // disagree — the cross-check that keeps combat markers honest if the log format shifts.
        foreach (var e in Fixtures.Table)
        {
            var (parser, events) = Fixtures.DriveFixture(e.Index);
            Assert.Equal(parser.CurrentMatchCombatStarts, parser.CurrentMatchBoardVisualState2);
            Assert.Equal(events.OfType<CombatStarted>().Count(), parser.CurrentMatchBoardVisualState2);
        }
    }

    [Fact]
    public void Truncated_match_ends_via_flush_with_no_terminal_state()
    {
        // match-17 is the client-restart truncation: no STATE=COMPLETE, so MatchEnded arrives only from Flush.
        var parser = new PowerLogParser(Fixtures.Seed);
        var feedEvents = new List<GameEvent>();
        foreach (var line in File.ReadLines(Fixtures.FixturePath(17)))
            feedEvents.AddRange(parser.Feed(line));

        Assert.Empty(feedEvents.OfType<MatchEnded>()); // nothing terminal while feeding

        var flush = parser.Flush().ToList();
        var end = Assert.Single(flush.OfType<MatchEnded>());
        Assert.True(end.Truncated);
        Assert.Equal(PlayState.Unknown, end.PlayState);
        Assert.Equal(1, end.FinalPlace);
    }

    [Fact]
    public void Date_cursor_rolls_over_midnight_between_matches()
    {
        // The corpus spans 2026-07-13 → 07-14; with the 21:56:22 seed, matches 1-4 start before midnight and
        // 5-17 after. Each fixture's MatchStarted date must land on the ground-truth day.
        foreach (var e in Fixtures.Table)
        {
            var (_, events) = Fixtures.DriveFixture(e.Index);
            var start = Assert.Single(events.OfType<MatchStarted>());
            Assert.Equal(2026, start.Timestamp.Year);
            Assert.Equal(7, start.Timestamp.Month);
            Assert.Equal(e.StartDay, start.Timestamp.Day);
        }
    }

    [Fact]
    public void Event_timestamps_are_monotonic_within_each_match()
    {
        // No spurious rollover mid-match: reconstructed wall-clock never goes backwards inside a single match.
        foreach (var e in Fixtures.Table)
        {
            var (_, events) = Fixtures.DriveFixture(e.Index);
            for (int i = 1; i < events.Count; i++)
                Assert.True(events[i].Timestamp >= events[i - 1].Timestamp,
                    $"match-{e.Index:D2}: event {i} ({events[i].GetType().Name}) went backwards in time");
        }
    }

    [Theory]
    [InlineData("GT_BATTLEGROUNDS", BgGameType.Solo)]
    [InlineData("GT_BATTLEGROUNDS_DUO", BgGameType.Duos)]
    [InlineData("GT_TAVERNBRAWL", BgGameType.NotBattlegrounds)]
    [InlineData("GT_RANKED", BgGameType.NotBattlegrounds)]
    public void Game_type_maps_correctly(string raw, BgGameType expected)
    {
        // The corpus is all-solo; this covers the duos and non-BG branches synthetically.
        var parser = new PowerLogParser(Fixtures.Seed);
        var events = new List<GameEvent>();
        events.AddRange(parser.Feed("D 21:59:32.7016092 GameState.DebugPrintPower() - CREATE_GAME"));
        events.AddRange(parser.Feed($"D 21:59:32.7016092 GameState.DebugPrintGame() - GameType={raw}"));

        var resolved = Assert.Single(events.OfType<GameTypeResolved>());
        Assert.Equal(expected, resolved.GameType);
    }

    [Fact]
    public void Next_create_game_closes_a_still_open_match_as_truncated()
    {
        // Two CREATE_GAMEs with no terminal state on the first: the first must be flushed truncated before
        // the second MatchStarted is emitted (in-stream, not deferred to Flush).
        var parser = new PowerLogParser(Fixtures.Seed);
        var events = new List<GameEvent>();
        events.AddRange(parser.Feed("D 21:59:32.7016092 GameState.DebugPrintPower() - CREATE_GAME"));
        events.AddRange(parser.Feed("D 21:59:33.0000000 GameState.DebugPrintGame() - GameType=GT_BATTLEGROUNDS"));
        events.AddRange(parser.Feed("D 22:45:16.1979778 GameState.DebugPrintPower() - CREATE_GAME"));

        var types = events.Select(e => e.GetType().Name).ToList();
        Assert.Equal(new[] { "MatchStarted", "GameTypeResolved", "MatchEnded", "MatchStarted" }, types);
        var truncated = events.OfType<MatchEnded>().Single();
        Assert.True(truncated.Truncated);
    }

    [Fact]
    public void Deferred_match_end_is_stamped_at_the_placement_settle_not_a_later_teardown_line()
    {
        // Regression (P2-9): after STATE=COMPLETE the game re-emits final placements, then tears the match
        // down. The deferred MatchEnded must carry the time of the LAST settle line (end of the placement
        // settle), not the later teardown line that merely triggers the emit.
        var parser = new PowerLogParser(Fixtures.Seed);
        var events = new List<GameEvent>();
        events.AddRange(parser.Feed("D 21:59:00.0000000 GameState.DebugPrintPower() - CREATE_GAME"));
        events.AddRange(parser.Feed("D 21:59:00.5000000 GameState.DebugPrintGame() - GameType=GT_BATTLEGROUNDS"));
        events.AddRange(parser.Feed("D 21:59:01.0000000 GameState.DebugPrintGame() - PlayerID=2, PlayerName=Tester#1234"));
        events.AddRange(parser.Feed("D 22:00:00.0000000 GameState.DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE"));
        // Placement settle (local player, id/player=2): this is the true end of the match.
        events.AddRange(parser.Feed("D 22:00:01.0000000 GameState.DebugPrintPower() - TAG_CHANGE Entity=[Player id=2 player=2] tag=PLAYER_LEADERBOARD_PLACE value=1"));
        // First non-leaderboard teardown line — arrives 4s later and only triggers the deferred end.
        events.AddRange(parser.Feed("D 22:00:05.0000000 GameState.DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value=FINAL_GAMEOVER"));

        var end = Assert.Single(events.OfType<MatchEnded>());
        Assert.False(end.Truncated);
        Assert.Equal(1, end.FinalPlace);

        var settle = Assert.Single(events.OfType<PlacementChanged>());
        Assert.Equal(settle.Timestamp, end.Timestamp);                       // stamped at the settle line
        Assert.Equal(new TimeSpan(0, 22, 0, 1), end.Timestamp.TimeOfDay);    // 22:00:01, the settle
        Assert.NotEqual(new TimeSpan(0, 22, 0, 5), end.Timestamp.TimeOfDay); // NOT 22:00:05, the teardown
    }

    // US Eastern (observes DST) for the DST-transition tests; tolerant of Windows vs IANA id.
    private static TimeZoneInfo Eastern()
    {
        foreach (var id in new[] { "Eastern Standard Time", "America/New_York" })
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz))
                return tz;
        throw new InvalidOperationException("US Eastern time zone not available on this host");
    }

    [Fact]
    public void Spring_forward_recomputes_the_offset_per_instant()
    {
        // P2-10: the UTC offset must track each reconstructed instant, not freeze at the session-start offset.
        // 2026-03-08 US Eastern springs forward at 02:00 EST (-05:00) → 03:00 EDT (-04:00).
        var seed = new DateTimeOffset(2026, 3, 8, 0, 30, 0, TimeSpan.FromHours(-5));
        var parser = new PowerLogParser(seed, Eastern());
        var events = new List<GameEvent>();
        events.AddRange(parser.Feed("D 01:00:00.0000000 GameState.DebugPrintPower() - CREATE_GAME"));
        events.AddRange(parser.Feed("D 01:30:00.0000000 GameState.DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=TURN value=1")); // before, EST
        events.AddRange(parser.Feed("D 03:30:00.0000000 GameState.DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=TURN value=3")); // after, EDT

        var turns = events.OfType<TurnStarted>().ToList();
        Assert.Equal(2, turns.Count);
        Assert.Equal(TimeSpan.FromHours(-5), turns[0].Timestamp.Offset); // pre-transition
        Assert.Equal(TimeSpan.FromHours(-4), turns[1].Timestamp.Offset); // post-transition (frozen-offset bug → -5)
        Assert.Equal(new DateTime(2026, 3, 8), turns[0].Timestamp.Date);
        Assert.Equal(new DateTime(2026, 3, 8), turns[1].Timestamp.Date); // forward jump is not a rollover
    }

    [Fact]
    public void Fall_back_rewind_is_not_treated_as_a_midnight_rollover()
    {
        // P2-10: 2026-11-01 US Eastern falls back at 02:00 EDT → 01:00 EST, so 01:59 → 01:00 is a <1h backward
        // step. It must NOT advance the date (the old "any backward jump = new day" rule wrongly added a day).
        var seed = new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-4));
        var parser = new PowerLogParser(seed, Eastern());
        var events = new List<GameEvent>();
        events.AddRange(parser.Feed("D 01:59:00.0000000 GameState.DebugPrintPower() - CREATE_GAME"));
        events.AddRange(parser.Feed("D 01:00:00.0000000 GameState.DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=TURN value=1"));

        var start = Assert.Single(events.OfType<MatchStarted>());
        var turn = Assert.Single(events.OfType<TurnStarted>());
        Assert.Equal(new DateTime(2026, 11, 1), start.Timestamp.Date);
        Assert.Equal(new DateTime(2026, 11, 1), turn.Timestamp.Date); // stays on 11-01 (bug → 11-02)
    }
}
