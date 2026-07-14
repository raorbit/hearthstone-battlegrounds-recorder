using System.Text.RegularExpressions;

namespace SpikeA.LogFidelity;

/// <summary>One parsed Battlegrounds match. Serialized to JSON as the parse output.</summary>
public sealed class MatchResult
{
    public int Index { get; set; }
    public string? StartTimestamp { get; set; }
    public string? EndTimestamp { get; set; }
    public string? GameType { get; set; }
    public bool IsOurGameType { get; set; }
    public string? LocalPlayerName { get; set; }
    public int? LocalPlayerId { get; set; }
    public int? HeroEntityId { get; set; }
    public string? HeroCardId { get; set; }
    public int? Place { get; set; }
    public int TavernTurns { get; set; }
    public int MaxRawTurn { get; set; }
    public int CombatCount { get; set; }
    public List<string> CombatTimestamps { get; set; } = new();
    /// <summary>Independent combat count via BOARD_VISUAL_STATE=2; should match CombatCount.</summary>
    public int BoardVisualCombatCount { get; set; }
    public string? Playstate { get; set; }
    public bool Truncated { get; set; }
    /// <summary>Set when a structural expectation was violated (never fatal).</summary>
    public List<string> Anomalies { get; set; } = new();
}

/// <summary>
/// Minimal, BG-only parser over GameState.DebugPrintPower/Game lines. Handles a stream that contains one
/// or many matches (splits on CREATE_GAME), so it works on a single-match fixture, a raw slice, or a whole
/// Power.log. Entity ids are reused across matches, so all per-entity state resets at each CREATE_GAME.
///
/// Heuristics grounded in the real log (see README):
///   local player  = the DebugPrintGame "PlayerName=" that contains '#' (a BattleTag); the other slot is an
///                   AI/placeholder name with no '#'. GameAccountId is NOT used (it is zeroed by the
///                   sanitizer), so sanitized and raw slices parse identically.
///   hero          = the local player's HERO_ENTITY value; take the last one at/before end of tavern turn 1
///                   (the TURN→2 transition) to absorb mulligan hero swaps; resolve id→cardId from inline
///                   entity descriptors.
///   tavern turns  = (max GameEntity TURN + 1) / 2.
///   combat starts = even GameEntity TURN transitions (coincide exactly with BOARD_VISUAL_STATE=2, the
///                   "board switched to combat view" signal — this client emits no STEP=MAIN_COMBAT).
///   placement     = last PLAYER_LEADERBOARD_PLACE for an entity that is one of the local player's hero
///                   entities (id match), or whose descriptor player= equals the local PlayerID.
///   end           = PLAYSTATE WON/LOST/CONCEDED for the local player; truncated when no STATE=COMPLETE.
/// </summary>
public sealed partial class BgMatchParser
{
    [GeneratedRegex(@"^GameType=(GT_\w+)")]
    private static partial Regex GameType();

    [GeneratedRegex(@"^PlayerID=(\d+), PlayerName=(.+?)\s*$")]
    private static partial Regex PlayerNameLine();

    [GeneratedRegex(@"^TAG_CHANGE Entity=(.+?) tag=HERO_ENTITY value=(\d+)")]
    private static partial Regex HeroEntity();

    [GeneratedRegex(@"\bid=(\d+)\b[^\]]*?\bcardId=(\w+)")]
    private static partial Regex EntityCard();

    [GeneratedRegex(@"^TAG_CHANGE Entity=GameEntity tag=TURN value=(\d+)")]
    private static partial Regex Turn();

    [GeneratedRegex(@"^TAG_CHANGE Entity=GameEntity tag=BOARD_VISUAL_STATE value=(\d+)")]
    private static partial Regex BoardVisual();

    [GeneratedRegex(@"^TAG_CHANGE Entity=\[(?<desc>[^\]]*)\] tag=PLAYER_LEADERBOARD_PLACE value=(\d+)")]
    private static partial Regex Leaderboard();

    [GeneratedRegex(@"^TAG_CHANGE Entity=(.+?) tag=PLAYSTATE value=(\w+)")]
    private static partial Regex Playstate();

    [GeneratedRegex(@"\bplayer=(\d+)")]
    private static partial Regex DescPlayer();

    private sealed class Builder
    {
        public MatchResult R = new();
        public DateTime? Start;
        public DateTime? Last;
        public string? LocalName;
        public int? LocalPlayerId;
        public readonly List<(DateTime When, int Id)> HeroEvents = new();
        public readonly HashSet<int> LocalHeroIds = new();
        public readonly Dictionary<int, string> CardMap = new();
        public int MaxTurn;
        public DateTime? FirstTurn2;
        public bool HasComplete;
    }

    public List<MatchResult> Parse(IEnumerable<string> lines, DateCursor cursor)
    {
        var results = new List<MatchResult>();
        Builder? b = null;
        int index = 0;

        void Finalize()
        {
            if (b is null) return;
            var r = b.R;
            r.LocalPlayerName = b.LocalName;
            r.LocalPlayerId = b.LocalPlayerId;
            r.MaxRawTurn = b.MaxTurn;
            r.TavernTurns = b.MaxTurn > 0 ? (b.MaxTurn + 1) / 2 : 0;
            r.StartTimestamp = b.Start?.ToString("yyyy-MM-ddTHH:mm:ss.fffffff");
            r.EndTimestamp = b.Last?.ToString("yyyy-MM-ddTHH:mm:ss.fffffff");
            r.Truncated = !b.HasComplete;
            r.IsOurGameType = r.GameType is "GT_BATTLEGROUNDS" or "GT_BATTLEGROUNDS_DUO";

            // Resolve hero: last HERO_ENTITY at/before end of tavern turn 1, else last overall.
            if (b.HeroEvents.Count > 0)
            {
                (DateTime When, int Id)? pick = null;
                if (b.FirstTurn2 is { } cut)
                    foreach (var e in b.HeroEvents)
                        if (e.When <= cut) pick = e;
                pick ??= b.HeroEvents[^1];
                r.HeroEntityId = pick.Value.Id;
                if (b.CardMap.TryGetValue(pick.Value.Id, out var card))
                    r.HeroCardId = card;
                else
                    r.Anomalies.Add($"hero entity {pick.Value.Id} had no resolvable cardId");
            }
            else if (b.LocalName is not null)
            {
                r.Anomalies.Add("no HERO_ENTITY tag change found for local player");
            }

            if (b.LocalName is null)
                r.Anomalies.Add("local player (PlayerName with '#') not found");
            if (r.Place is null && r.IsOurGameType)
                r.Anomalies.Add("no leaderboard placement resolved for local hero");
            if (r.CombatCount != r.BoardVisualCombatCount)
                r.Anomalies.Add($"combat count {r.CombatCount} != BOARD_VISUAL_STATE=2 count {r.BoardVisualCombatCount}");

            results.Add(r);
            b = null;
        }

        foreach (var raw in lines)
        {
            if (!LogLine.TryParse(raw, out var ln)) continue;
            if (!ln.IsGameState) continue; // GameState stream only

            var when = cursor.Advance(ln.TimeOfDay);
            string p = ln.Payload;

            if (ln.IsGameStatePower && p.StartsWith("CREATE_GAME", StringComparison.Ordinal))
            {
                Finalize();
                index++;
                b = new Builder();
                b.R.Index = index;
                b.Start = when;
                b.Last = when;
                continue;
            }

            if (b is null) continue; // pre-first-CREATE_GAME noise
            b.Last = when;

            // --- DebugPrintGame lines: game type + player identity ---
            if (ln.IsGameStateGame)
            {
                var gt = GameType().Match(p);
                if (gt.Success) { b.R.GameType ??= gt.Groups[1].Value; continue; }
                var pn = PlayerNameLine().Match(p);
                if (pn.Success && pn.Groups[2].Value.Contains('#'))
                {
                    b.LocalName = pn.Groups[2].Value;
                    b.LocalPlayerId = int.Parse(pn.Groups[1].Value);
                }
                continue;
            }

            // --- DebugPrintPower lines ---
            // Build id→cardId map from any inline entity descriptor(s) on this line.
            foreach (Match ec in EntityCard().Matches(p))
                b.CardMap[int.Parse(ec.Groups[1].Value)] = ec.Groups[2].Value;

            if (p.StartsWith("TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE", StringComparison.Ordinal))
            {
                b.HasComplete = true;
                continue;
            }

            var turn = Turn().Match(p);
            if (turn.Success)
            {
                int v = int.Parse(turn.Groups[1].Value);
                if (v > b.MaxTurn) b.MaxTurn = v;
                if (v % 2 == 0)
                {
                    b.R.CombatCount++;
                    b.R.CombatTimestamps.Add(when.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"));
                    if (v == 2 && b.FirstTurn2 is null) b.FirstTurn2 = when;
                }
                continue;
            }

            var bv = BoardVisual().Match(p);
            if (bv.Success)
            {
                if (bv.Groups[1].Value == "2") b.R.BoardVisualCombatCount++;
                continue;
            }

            var he = HeroEntity().Match(p);
            if (he.Success && b.LocalName is not null && he.Groups[1].Value == b.LocalName)
            {
                int id = int.Parse(he.Groups[2].Value);
                b.HeroEvents.Add((when, id));
                b.LocalHeroIds.Add(id);
                continue;
            }

            var lb = Leaderboard().Match(p);
            if (lb.Success)
            {
                string desc = lb.Groups["desc"].Value;
                int place = int.Parse(lb.Groups[1].Value);
                int? entId = null;
                var idOnly = Regex.Match(desc, @"\bid=(\d+)");
                if (idOnly.Success) entId = int.Parse(idOnly.Groups[1].Value);
                var plm = DescPlayer().Match(desc);
                bool isLocal =
                    (entId is { } e && b.LocalHeroIds.Contains(e)) ||
                    (plm.Success && b.LocalPlayerId is { } lp && int.Parse(plm.Groups[1].Value) == lp);
                if (isLocal) b.R.Place = place;
                continue;
            }

            var ps = Playstate().Match(p);
            if (ps.Success && b.LocalName is not null && ps.Groups[1].Value == b.LocalName)
            {
                string state = ps.Groups[2].Value;
                if (state is "WON" or "LOST" or "CONCEDED")
                    b.R.Playstate = state;
                continue;
            }
        }

        Finalize();
        return results;
    }
}
