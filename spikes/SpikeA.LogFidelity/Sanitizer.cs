using System.Text.RegularExpressions;

namespace SpikeA.LogFidelity;

/// <summary>
/// Redacts private data from a Power.log slice before it can enter Git.
///
/// Player handles occur in two forms in a Battlegrounds Power.log, both redacted here:
///   - The LOCAL player's BattleTag, "name#digits" (e.g. "Player1#1234"): a name token followed by "#"
///     and digits. Each distinct BattleTag maps to PlayerN#00000. The "#" is retained on purpose — the
///     parser keys local-player detection off a PlayerName that contains "#".
///   - OPPONENT display names, which carry NO "#". A real handle appears as a bare Entity= token on a
///     player-scoped TAG_CHANGE line (HERO_ENTITY / PLAYSTATE / TURN — the opponent you face each combat)
///     and as the nominal "PlayerID=N, PlayerName=&lt;handle&gt;" slot. Dozens of distinct opponents occur
///     across a session (linked to hero and win/loss), so redacting only the BattleTag is NOT enough. These
///     are learned from the full raw slice by <see cref="LearnPlayers"/> and each maps to a bare PlayerN.
///
/// Game constructs that share those tag lines are NOT players and are left intact: "GameEntity", numeric
/// entity ids, bracketed "[entityName=… cardId=…]" card/minion descriptors, and the tavern-keeper / ghost
/// bots "Bartender Bob" (cardId TB_BaconShopBob) and "Kel'Thuzad" (cardId TB_BaconShop_HERO_KelThuzad). A
/// handle is told apart from a construct structurally: a construct's name also occurs as a carded
/// "entityName=" descriptor somewhere in the slice, a real handle never does.
///
/// Also redacted: GameAccountId=[hi=&lt;n&gt; lo=&lt;n&gt;] (both zeroed) and Windows user paths (defensive).
///
/// A new Sanitizer is created per match file so the PlayerN numbering restarts at 1 for each fixture.
/// <see cref="LearnPlayers"/> must be called with that match's full raw slice before <see cref="Sanitize"/>.
/// </summary>
public sealed partial class Sanitizer
{
    private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);
    private int _next = 1;

    // Name token + "#" + digits. Lookbehind avoids matching inside an already-numbered token.
    // Uses Unicode letter/number classes so accented/CJK tags like "Ünïcödë#123" are caught too.
    [GeneratedRegex(@"(?<![\w#])[\p{L}\p{N}_]+#\d+\b")]
    private static partial Regex BattleTag();

    [GeneratedRegex(@"GameAccountId=\[hi=\d+ lo=\d+\]")]
    private static partial Regex GameAccount();

    [GeneratedRegex(@"[A-Za-z]:\\Users\\[^\s""\]]+")]
    private static partial Regex WindowsPath();

    // A bare (non-bracketed) Entity= token immediately before " tag=". The "[^\[\]]" body means an
    // "Entity=[entityName=… ]" descriptor never matches (its first char is "[").
    [GeneratedRegex(@"Entity=(?<name>[^\[\]]+?) tag=")]
    private static partial Regex EntityName();

    // The nominal player-slot name on a GameState.DebugPrintGame line ("PlayerID=N, PlayerName=<handle>").
    [GeneratedRegex(@"PlayerName=(?<name>.+?)\s*$")]
    private static partial Regex PlayerNameField();

    // A carded entity descriptor: "entityName=<cardName> id=<n>". A card/minion/hero name — never a handle.
    [GeneratedRegex(@"entityName=(?<name>[^\[\]]+?) id=\d+\b")]
    private static partial Regex CardedName();

    /// <summary>
    /// Pre-scan the FULL raw slice of one match to learn its distinct opponent display names (the player
    /// handles that carry no BattleTag). Must run before <see cref="Sanitize"/>. Reserves Player1 for the
    /// local BattleTag, then numbers opponents Player2, Player3, … in first-appearance order.
    /// The full slice is required: a construct's carded "entityName=" descriptor (e.g. Bartender Bob's) can
    /// be dropped by distillation, so it would be missing if only the committed lines were scanned.
    /// </summary>
    public void LearnPlayers(IEnumerable<string> rawMatchLines)
    {
        var lines = rawMatchLines as IReadOnlyList<string> ?? rawMatchLines.ToList();

        // Names attached to a card/minion/hero descriptor — never a player handle (Bartender Bob,
        // Kel'Thuzad, and every hero/minion card).
        var carded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (!line.Contains("entityName=", StringComparison.Ordinal) ||
                !line.Contains("cardId=", StringComparison.Ordinal))
                continue;
            foreach (Match m in CardedName().Matches(line))
                carded.Add(m.Groups["name"].Value);
        }

        // Pass 1: reserve numbers for BattleTags in first-appearance order (the local player → Player1).
        foreach (var line in lines)
            foreach (Match m in BattleTag().Matches(line))
                if (!_map.ContainsKey(m.Value))
                    _map[m.Value] = $"Player{_next++}#00000";

        // Pass 2: opponent handles — bare Entity= tokens on TAG_CHANGE lines and nominal PlayerName= slots,
        // excluding carded constructs, "GameEntity", numeric entity ids, and anything already a BattleTag.
        void Consider(string name)
        {
            if (name.Length == 0 || _map.ContainsKey(name)) return;
            if (name == "GameEntity") return;
            if (name.Contains('#')) return;               // BattleTags are handled in pass 1
            if (carded.Contains(name)) return;            // Bartender Bob, Kel'Thuzad, card/minion names
            if (name.All(char.IsAsciiDigit)) return;      // bare entity ids
            _map[name] = $"Player{_next++}";
        }

        foreach (var line in lines)
        {
            if (line.Contains("TAG_CHANGE Entity=", StringComparison.Ordinal))
                foreach (Match m in EntityName().Matches(line))
                    Consider(m.Groups["name"].Value);
            if (line.Contains("PlayerName=", StringComparison.Ordinal))
            {
                var pm = PlayerNameField().Match(line);
                if (pm.Success) Consider(pm.Groups["name"].Value);
            }
        }
    }

    public string Sanitize(string line)
    {
        // Local player's BattleTag → PlayerN#00000 (retains '#' so the parser still recognises the local
        // player). LearnPlayers has usually pre-registered the mapping; assign lazily otherwise.
        line = BattleTag().Replace(line, m =>
        {
            if (!_map.TryGetValue(m.Value, out var repl))
            {
                repl = $"Player{_next++}#00000";
                _map[m.Value] = repl;
            }
            return repl;
        });

        // Opponent bare Entity= handles → PlayerN. Constructs (GameEntity, Bartender Bob, numeric ids) are
        // absent from the map and pass through unchanged.
        line = EntityName().Replace(line, m =>
            _map.TryGetValue(m.Groups["name"].Value, out var repl)
                ? $"Entity={repl} tag="
                : m.Value);

        // Opponent nominal PlayerName= slot → PlayerN (the local slot is already the "#" form above).
        line = PlayerNameField().Replace(line, m =>
            _map.TryGetValue(m.Groups["name"].Value, out var repl)
                ? $"PlayerName={repl}"
                : m.Value);

        line = GameAccount().Replace(line, "GameAccountId=[hi=0 lo=0]");
        line = WindowsPath().Replace(line, "<redacted-path>");
        return line;
    }

    /// <summary>The stable original → placeholder mapping applied to this file (for reporting).</summary>
    public IReadOnlyDictionary<string, string> Mapping => _map;
}
