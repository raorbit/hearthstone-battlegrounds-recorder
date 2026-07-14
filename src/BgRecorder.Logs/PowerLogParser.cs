using System.Text.RegularExpressions;
using BgRecorder.Core.Events;

namespace BgRecorder.Logs;

/// <summary>
/// Line-level, streaming Battlegrounds parser over the GameState.DebugPrint* stream. Feed one raw Power.log
/// line at a time; each call yields zero or more <see cref="GameEvent"/>s in log order, with wall-clock
/// timestamps reconstructed from a date cursor seeded with the session-folder start time. Usable standalone
/// (fixtures, tests) or driven by <see cref="GameEventSource"/> over a live tail.
///
/// Verified log facts (Spike A, 17 real matches):
///   * Parse GameState.DebugPrint* lines only (PowerTaskList is a delayed replay).
///   * Entity ids are reused across matches — all per-match state resets at CREATE_GAME.
///   * Local player = the DebugPrintGame PlayerName containing '#'.
///   * Displayed tavern turn = (rawTurn + 1) / 2.
///   * Combat start = even GameEntity TURN transition (coincides with BOARD_VISUAL_STATE=2, kept as a
///     cross-check; STEP=MAIN_COMBAT does not exist in current BG logs).
///   * Placement = PLAYER_LEADERBOARD_PLACE for the local hero entity (by hero-entity id or descriptor
///     player=, tracking mulligan hero swaps).
///   * Match end = terminal PLAYSTATE (WON/LOST/CONCEDED) for the local player and GameEntity STATE=COMPLETE;
///     truncated when the stream ends (next CREATE_GAME, a session change, or source close) with no COMPLETE.
/// </summary>
public sealed partial class PowerLogParser
{
    // --- payload regexes (ported verbatim from the Spike A parser) ---
    [GeneratedRegex(@"^GameType=(GT_\w+)")]
    private static partial Regex GameTypeRe();

    [GeneratedRegex(@"^PlayerID=(\d+), PlayerName=(.+?)\s*$")]
    private static partial Regex PlayerNameRe();

    [GeneratedRegex(@"^TAG_CHANGE Entity=(.+?) tag=HERO_ENTITY value=(\d+)")]
    private static partial Regex HeroEntityRe();

    [GeneratedRegex(@"\bid=(\d+)\b[^\]]*?\bcardId=(\w+)")]
    private static partial Regex EntityCardRe();

    [GeneratedRegex(@"^TAG_CHANGE Entity=GameEntity tag=TURN value=(\d+)")]
    private static partial Regex TurnRe();

    [GeneratedRegex(@"^TAG_CHANGE Entity=GameEntity tag=BOARD_VISUAL_STATE value=(\d+)")]
    private static partial Regex BoardVisualRe();

    [GeneratedRegex(@"^TAG_CHANGE Entity=\[(?<desc>[^\]]*)\] tag=PLAYER_LEADERBOARD_PLACE value=(\d+)")]
    private static partial Regex LeaderboardRe();

    [GeneratedRegex(@"^TAG_CHANGE Entity=(.+?) tag=PLAYSTATE value=(\w+)")]
    private static partial Regex PlaystateRe();

    [GeneratedRegex(@"\bid=(\d+)")]
    private static partial Regex IdRe();

    [GeneratedRegex(@"\bplayer=(\d+)")]
    private static partial Regex DescPlayerRe();

    private readonly DateCursor _cursor;
    private readonly TimeZoneInfo _timeZone;

    // --- per-match state (reset at every CREATE_GAME) ---
    private bool _matchOpen;
    private bool _endPending;           // STATE=COMPLETE seen; awaiting the endgame placement settle
    private DateTimeOffset _lastLineTime;
    private string? _localName;
    private int? _localPlayerId;
    private BgGameType? _gameType;
    private readonly Dictionary<int, string> _cardMap = new();
    private readonly HashSet<int> _localHeroIds = new();
    private int? _pendingHeroId;
    private DateTimeOffset _pendingHeroTime;
    private string? _lastHeroCardId;
    private int _combatStarts;
    private int _boardVisual2;
    private int? _currentPlace;
    private PlayState _localPlayState;

    /// <param name="sessionStart">
    /// The session log folder's start timestamp (from its <c>Hearthstone_YYYY_MM_DD_HH_MM_SS</c> name).
    /// Only its date and time-of-day seed the date cursor; the offset is recomputed per reconstructed
    /// instant (see <paramref name="timeZone"/>), so an event's stamp stays correct across a DST change.
    /// </param>
    /// <param name="timeZone">
    /// Time zone used to resolve the UTC offset for each reconstructed local instant. Defaults to
    /// <see cref="TimeZoneInfo.Local"/>; a test seam so fixtures can pin a specific zone (e.g. to exercise
    /// DST transitions) deterministically.
    /// </param>
    public PowerLogParser(DateTimeOffset sessionStart, TimeZoneInfo? timeZone = null)
    {
        _cursor = new DateCursor(DateOnly.FromDateTime(sessionStart.DateTime), sessionStart.TimeOfDay);
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    /// <summary>Even-TURN transitions counted for the current (or most recent) match. Test cross-check.</summary>
    public int CurrentMatchCombatStarts => _combatStarts;

    /// <summary>BOARD_VISUAL_STATE=2 signals counted for the current (or most recent) match. Should equal
    /// <see cref="CurrentMatchCombatStarts"/> — an independent cross-check on combat-boundary detection.</summary>
    public int CurrentMatchBoardVisualState2 => _boardVisual2;

    /// <summary>Feed one raw log line; yields the events it produces, in order.</summary>
    public IEnumerable<GameEvent> Feed(string rawLine)
    {
        if (!LogLine.TryParse(rawLine, out var ln) || !ln.IsGameState)
            yield break;

        var when = Stamp(_cursor.Advance(ln.TimeOfDay));
        string p = ln.Payload;

        // CREATE_GAME opens a new match; close any still-open match first.
        if (ln.IsGameStatePower && p.StartsWith("CREATE_GAME", StringComparison.Ordinal))
        {
            foreach (var e in CloseOpenMatch()) yield return e;
            ResetMatch();
            _matchOpen = true;
            _lastLineTime = when;
            yield return new MatchStarted(when);
            yield break;
        }

        if (!_matchOpen)
            yield break; // noise before the first CREATE_GAME, or after a match closed

        // A completed match is awaiting the endgame placement settle: the game reassigns final placements as
        // a burst of PLAYER_LEADERBOARD_PLACE lines right after STATE=COMPLETE (for players eliminated mid-game
        // the true placement only appears here, after COMPLETE). The first non-leaderboard line marks the burst
        // over, so emit the deferred MatchEnded — carrying the now-final placement — before processing it.
        // NOTE: _lastLineTime is updated only AFTER this check, so the deferred end is stamped with the last
        // settle line (the end of the placement settle) rather than the triggering teardown line.
        if (_endPending && !p.Contains("PLAYER_LEADERBOARD_PLACE", StringComparison.Ordinal))
        {
            foreach (var e in EmitPendingEnd()) yield return e;
            yield break; // match closed; the triggering (teardown) line carries nothing we consume
        }

        _lastLineTime = when;

        // --- DebugPrintGame: game type + local player identity ---
        if (ln.IsGameStateGame)
        {
            var gt = GameTypeRe().Match(p);
            if (gt.Success)
            {
                if (_gameType is null)
                {
                    _gameType = MapGameType(gt.Groups[1].Value);
                    yield return new GameTypeResolved(when, _gameType.Value);
                }
                yield break;
            }

            var pn = PlayerNameRe().Match(p);
            if (pn.Success && pn.Groups[2].Value.Contains('#'))
            {
                _localName = pn.Groups[2].Value;
                _localPlayerId = int.Parse(pn.Groups[1].Value);
            }
            yield break;
        }

        // --- DebugPrintPower ---
        // Learn id→cardId from any inline entity descriptor(s) on this line.
        foreach (Match ec in EntityCardRe().Matches(p))
            _cardMap[int.Parse(ec.Groups[1].Value)] = ec.Groups[2].Value;

        // Emit the local hero once its cardId is resolvable (the HERO_ENTITY tag and the descriptor that
        // carries the cardId are usually on adjacent lines).
        if (_pendingHeroId is { } pend && _cardMap.TryGetValue(pend, out var pendCard) && pendCard != _lastHeroCardId)
        {
            _lastHeroCardId = pendCard;
            _pendingHeroId = null;
            yield return new LocalHeroResolved(_pendingHeroTime, pendCard);
        }

        if (p.StartsWith("TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE", StringComparison.Ordinal))
        {
            // Defer MatchEnded until the placement settle finishes (see the _endPending check above).
            _endPending = true;
            yield break;
        }

        var turn = TurnRe().Match(p);
        if (turn.Success)
        {
            int v = int.Parse(turn.Groups[1].Value);
            int tavern = (v + 1) / 2;
            yield return new TurnStarted(when, v, tavern);
            if (v % 2 == 0)
            {
                _combatStarts++;
                yield return new CombatStarted(when, tavern);
            }
            yield break;
        }

        var bv = BoardVisualRe().Match(p);
        if (bv.Success)
        {
            if (bv.Groups[1].Value == "2") _boardVisual2++;
            yield break;
        }

        var he = HeroEntityRe().Match(p);
        if (he.Success && _localName is not null && he.Groups[1].Value == _localName)
        {
            int id = int.Parse(he.Groups[2].Value);
            _localHeroIds.Add(id);
            _pendingHeroId = id;
            _pendingHeroTime = when;
            // cardId may already be known (descriptor on the same line) — emit immediately if so.
            if (_cardMap.TryGetValue(id, out var card) && card != _lastHeroCardId)
            {
                _lastHeroCardId = card;
                _pendingHeroId = null;
                yield return new LocalHeroResolved(when, card);
            }
            yield break;
        }

        var lb = LeaderboardRe().Match(p);
        if (lb.Success)
        {
            string desc = lb.Groups["desc"].Value;
            int place = int.Parse(lb.Groups[1].Value);
            int? entId = null;
            var idOnly = IdRe().Match(desc);
            if (idOnly.Success) entId = int.Parse(idOnly.Groups[1].Value);
            var plm = DescPlayerRe().Match(desc);
            bool isLocal =
                (entId is { } e && _localHeroIds.Contains(e)) ||
                (plm.Success && _localPlayerId is { } lp && int.Parse(plm.Groups[1].Value) == lp);
            if (isLocal && place != _currentPlace)
            {
                _currentPlace = place;
                yield return new PlacementChanged(when, place);
            }
            yield break;
        }

        var ps = PlaystateRe().Match(p);
        if (ps.Success && _localName is not null && ps.Groups[1].Value == _localName)
        {
            _localPlayState = ps.Groups[2].Value switch
            {
                "WON" => PlayState.Won,
                "LOST" => PlayState.Lost,
                "CONCEDED" => PlayState.Conceded,
                _ => _localPlayState,
            };
        }
    }

    /// <summary>
    /// Signals end of the source stream. Emits a final <see cref="MatchEnded"/> for a match that is still
    /// open: non-truncated (with the settled placement) if STATE=COMPLETE was seen but the settle never hit a
    /// trailing non-leaderboard line, otherwise truncated. Called by <see cref="GameEventSource"/> on stop and
    /// on session change; safe to call when no match is open (yields nothing).
    /// </summary>
    public IEnumerable<GameEvent> Flush() => CloseOpenMatch();

    /// <summary>Closes a still-open match: the deferred non-truncated end if pending, else a truncated end.</summary>
    private IEnumerable<GameEvent> CloseOpenMatch()
    {
        if (_endPending)
        {
            foreach (var e in EmitPendingEnd()) yield return e;
        }
        else if (_matchOpen)
        {
            _matchOpen = false;
            yield return new MatchEnded(_lastLineTime, _currentPlace, _localPlayState, Truncated: true);
        }
    }

    private IEnumerable<GameEvent> EmitPendingEnd()
    {
        _endPending = false;
        _matchOpen = false;
        // Stamp with the last processed line (the end of the placement settle) so the event never precedes
        // the settle placements it accounts for.
        yield return new MatchEnded(_lastLineTime, _currentPlace, _localPlayState, Truncated: false);
    }

    // Resolve the UTC offset for this specific reconstructed local instant (dt has Unspecified kind), rather
    // than freezing the session-start offset — so events on either side of a DST change stamp correctly.
    // Ambiguous fall-back times resolve to standard time and invalid spring-forward gap times to the pre-gap
    // offset per TimeZoneInfo.GetUtcOffset; log timestamps never land in the gap, so that never bites.
    private DateTimeOffset Stamp(DateTime dt) => new(dt, _timeZone.GetUtcOffset(dt));

    private static BgGameType MapGameType(string gt) => gt switch
    {
        "GT_BATTLEGROUNDS" => BgGameType.Solo,
        "GT_BATTLEGROUNDS_DUO" => BgGameType.Duos,
        _ => BgGameType.NotBattlegrounds,
    };

    private void ResetMatch()
    {
        _matchOpen = false;
        _endPending = false;
        _localName = null;
        _localPlayerId = null;
        _gameType = null;
        _cardMap.Clear();
        _localHeroIds.Clear();
        _pendingHeroId = null;
        _lastHeroCardId = null;
        _combatStarts = 0;
        _boardVisual2 = 0;
        _currentPlace = null;
        _localPlayState = PlayState.Unknown;
    }
}
