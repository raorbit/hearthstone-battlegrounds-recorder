using System.Text.RegularExpressions;

namespace SpikeA.LogFidelity;

/// <summary>
/// Redacts private data from a Power.log slice before it can enter Git.
///
/// Grounded against the real log:
///   - BattleTags appear only as the local player's tag (e.g. "Player1"): a name token followed by
///     "#" and digits. Opponents are shown by hero name, never a BattleTag, so the only "#N" tokens are
///     the human's. Each distinct BattleTag maps to PlayerN#00000 with a stable per-file mapping.
///   - GameAccountId=[hi=&lt;n&gt; lo=&lt;n&gt;] carries the account's real high/low IDs (the non-zero pair is the
///     local account); both are zeroed.
///   - Windows user paths (X:\Users\...) are redacted defensively (none observed in Power.log, but the
///     redaction is part of the contract).
///
/// A new Sanitizer is created per match file so the PlayerN numbering restarts at 1 for each fixture.
/// </summary>
public sealed partial class Sanitizer
{
    private readonly Dictionary<string, string> _tagMap = new(StringComparer.Ordinal);
    private int _next = 1;

    // Name token + "#" + digits. Lookbehind avoids matching inside an already-numbered token.
    // Uses Unicode letter/number classes so tags like "Player#123" would also be caught.
    [GeneratedRegex(@"(?<![\w#])[\p{L}\p{N}_]+#\d+\b")]
    private static partial Regex BattleTag();

    [GeneratedRegex(@"GameAccountId=\[hi=\d+ lo=\d+\]")]
    private static partial Regex GameAccount();

    [GeneratedRegex(@"[A-Za-z]:\\Users\\[^\s""\]]+")]
    private static partial Regex WindowsPath();

    public string Sanitize(string line)
    {
        line = BattleTag().Replace(line, m =>
        {
            if (!_tagMap.TryGetValue(m.Value, out var repl))
            {
                repl = $"Player{_next++}#00000";
                _tagMap[m.Value] = repl;
            }
            return repl;
        });
        line = GameAccount().Replace(line, "GameAccountId=[hi=0 lo=0]");
        line = WindowsPath().Replace(line, "<redacted-path>");
        return line;
    }

    /// <summary>The stable BattleTag → placeholder mapping applied to this file (for reporting).</summary>
    public IReadOnlyDictionary<string, string> Mapping => _tagMap;
}
