using BgRecorder.Core.Events;
using BgRecorder.Logs;

namespace BgRecorder.Logs.Tests;

/// <summary>
/// Ground truth for the 17-match sanitized fixture corpus and helpers to drive the parser over it.
///
/// The expected table below was derived by running the salvaged Spike A parser over the committed
/// sanitized fixtures and transcribing its verdicts verbatim:
///   dotnet run --project spikes/SpikeA.LogFidelity -- parse spikes/SpikeA.LogFidelity/fixtures/sanitized
/// (seed 2026-07-13 21:56:22, a fresh cursor per single-match fixture — exactly what these tests replay).
/// Columns: hero cardId, final placement, displayed tavern turns = (maxRawTurn+1)/2, combat count = even
/// raw-TURN transitions (== BOARD_VISUAL_STATE=2 count for every match), game type, truncation, play state.
/// All 17 are solo Battlegrounds (GT_BATTLEGROUNDS); the corpus contains no duos match, so the duos and
/// non-BG game-type mappings are covered by a separate synthetic unit test. match-10's local player conceded
/// then immediately flipped to LOST (last terminal state wins → Lost). match-17 is truncated (client cut off
/// mid-match: no STATE=COMPLETE), so it has no terminal play state and its end is synthesised on Flush.
/// </summary>
public static class Fixtures
{
    /// <summary>The session-folder seed the Spike A ground truth used; these tests replay it per fixture.</summary>
    public static readonly DateTimeOffset Seed = new(2026, 7, 13, 21, 56, 22, TimeSpan.Zero);

    public sealed record Expected(
        int Index,
        string Hero,
        int Place,
        int TavernTurns,
        int Combat,
        BgGameType GameType,
        bool Truncated,
        PlayState PlayState,
        int StartDay); // day-of-month the match starts on (13 before midnight, 14 after)

    public static readonly IReadOnlyList<Expected> Table = new[]
    {
        new Expected(1,  "TB_BaconShop_HERO_21",         2, 19, 19, BgGameType.Solo, false, PlayState.Lost,    13),
        new Expected(2,  "BG31_HERO_005",                7, 10, 10, BgGameType.Solo, false, PlayState.Lost,    13),
        new Expected(3,  "TB_BaconShop_HERO_78",         1, 13, 13, BgGameType.Solo, false, PlayState.Won,     13),
        new Expected(4,  "TB_BaconShop_HERO_62",         1, 14, 14, BgGameType.Solo, false, PlayState.Won,     13),
        new Expected(5,  "BG20_HERO_242",                3, 12, 12, BgGameType.Solo, false, PlayState.Lost,    14),
        new Expected(6,  "BG20_HERO_201",                4, 13, 13, BgGameType.Solo, false, PlayState.Lost,    14),
        new Expected(7,  "TB_BaconShop_HERO_40",         2, 15, 15, BgGameType.Solo, false, PlayState.Lost,    14),
        new Expected(8,  "BG22_HERO_004_SKIN_A",         2, 12, 12, BgGameType.Solo, false, PlayState.Lost,    14),
        new Expected(9,  "TB_BaconShop_HERO_28",         5, 11, 11, BgGameType.Solo, false, PlayState.Lost,    14),
        new Expected(10, "TB_BaconShop_HERO_58_SKIN_C4", 5, 11, 10, BgGameType.Solo, false, PlayState.Lost,    14),
        new Expected(11, "BG22_HERO_001",                2, 16, 16, BgGameType.Solo, false, PlayState.Lost,    14),
        new Expected(12, "BG34_HERO_002",                5, 11, 11, BgGameType.Solo, false, PlayState.Lost,    14),
        new Expected(13, "TB_BaconShop_HERO_57_SKIN_C4", 2, 13, 13, BgGameType.Solo, false, PlayState.Lost,    14),
        new Expected(14, "BG22_HERO_201",                8,  8,  8, BgGameType.Solo, false, PlayState.Lost,    14),
        new Expected(15, "BG30_HERO_304",                1, 14, 14, BgGameType.Solo, false, PlayState.Won,     14),
        new Expected(16, "BG27_HERO_801",                3, 14, 14, BgGameType.Solo, false, PlayState.Lost,    14),
        new Expected(17, "BG34_HERO_004",                1, 16, 15, BgGameType.Solo, true,  PlayState.Unknown, 14),
    };

    /// <summary>Absolute path to the committed sanitized fixture directory (found by walking up the tree).</summary>
    public static string SanitizedDir { get; } = LocateSanitizedDir();

    public static string FixturePath(int index) => Path.Combine(SanitizedDir, $"match-{index:D2}.txt");

    /// <summary>Feed a whole single-match fixture through a fresh parser; returns the parser and its events.</summary>
    public static (PowerLogParser Parser, List<GameEvent> Events) DriveFixture(int index)
    {
        var parser = new PowerLogParser(Seed);
        var events = new List<GameEvent>();
        foreach (var line in File.ReadLines(FixturePath(index)))
            events.AddRange(parser.Feed(line));
        events.AddRange(parser.Flush());
        return (parser, events);
    }

    private static string LocateSanitizedDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "spikes", "SpikeA.LogFidelity", "fixtures", "sanitized");
            if (File.Exists(Path.Combine(candidate, "match-01.txt")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate spikes/SpikeA.LogFidelity/fixtures/sanitized above " + AppContext.BaseDirectory);
    }
}
