namespace SpikeA.LogFidelity;

/// <summary>
/// Decides which raw log lines are kept in a distilled fixture.
///
/// Why distill: a single full Battlegrounds match is ~50 MB of Power.log (every shop roll, every minion
/// stat, printed twice — GameState + PowerTaskList). The 17-match corpus is ~895 MB, which must never enter
/// Git. The full raw slices stay in the gitignored fixtures/raw/. The committed fixtures/sanitized/ corpus
/// keeps only the GameState lines the BG parser actually consumes, so it is a few MB total, still parses to
/// identical structural results, and is a sane permanent regression corpus.
///
/// Kept:
///   - every GameState.DebugPrintGame line (GameType, PlayerID/PlayerName) — small,
///   - GameState.DebugPrintPower lines that carry a BG boundary/metadata tag the parser reads:
///     CREATE_GAME, TURN, BOARD_VISUAL_STATE, HERO_ENTITY, PLAYER_LEADERBOARD_PLACE (also the sole carrier
///     of the local hero's cardId descriptor), PLAYSTATE, STATE.
/// PowerTaskList (delayed replay) lines are always dropped.
/// </summary>
public static class LineFilter
{
    private static readonly string[] KeptPowerTags =
    {
        "tag=TURN value=",
        "tag=BOARD_VISUAL_STATE value=",
        "tag=HERO_ENTITY value=",
        "tag=PLAYER_LEADERBOARD_PLACE value=",
        "tag=PLAYSTATE value=",
        "tag=STATE value=",
    };

    public static bool IsParserRelevant(string rawLine)
    {
        if (!LogLine.TryParse(rawLine, out var ln) || !ln.IsGameState)
            return false;
        if (ln.IsGameStateGame)
            return true;
        if (!ln.IsGameStatePower)
            return false;

        string p = ln.Payload;
        if (p.StartsWith("CREATE_GAME", StringComparison.Ordinal))
            return true;
        foreach (var t in KeptPowerTags)
            if (p.Contains(t, StringComparison.Ordinal))
                return true;
        return false;
    }
}
