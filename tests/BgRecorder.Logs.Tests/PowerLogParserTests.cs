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
}
