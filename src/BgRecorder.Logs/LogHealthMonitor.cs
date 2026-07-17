namespace BgRecorder.Logs;

/// <summary>
/// The game-patch tripwire (plan risk #2): raises one alert when a game VISIBLY BEGAN in the raw log
/// but the parser produced nothing for it — the "silently missing every match" failure.
///
/// The detector must first ARM: it arms only on a raw line that carries both the
/// <c>GameState.DebugPrint</c> logger name and the <c>CREATE_GAME</c> token yet yielded no event. A
/// healthy parser turns exactly that line into <see cref="Core.Events.MatchStarted"/> (so it is never
/// reported here as silent), which is what makes the rule effectively false-positive-free:
/// <list type="bullet">
/// <item>Mid/late-match silent stretches CANNOT trip it — measured against the raw Spike A corpus, a
/// healthy late-game recruit phase runs up to ~19,000 event-free GameState lines over ~4 minutes, so
/// any threshold on "silent traffic" alone cries wolf; those stretches never contain CREATE_GAME and
/// so never arm the detector.</item>
/// <item>Attaching mid-match (recorder started while a game is running; the tail seeks to end and the
/// parser never sees the game's start) stays silent until the NEXT game — which either parses
/// (healthy) or arms the detector (a true positive).</item>
/// </list>
/// Accepted blind spot: a patch that renames the CREATE_GAME token itself leaves this detector blind —
/// it watches for the far more common case (line-prefix / surrounding-format changes) where the token
/// text survives but the line no longer parses.
/// </summary>
public sealed class LogHealthMonitor
{
    private const string GameStateMarker = "GameState.DebugPrint";
    private const string GameStartMarker = "CREATE_GAME";

    private readonly int _minLines;
    private readonly TimeSpan _window;

    private bool _armed;
    private int _silentLinesSinceArmed;
    private DateTimeOffset _armedAt;
    private bool _latched;

    /// <param name="minLines">
    /// GameState lines that must follow the unparsed game start, still event-free, before alerting —
    /// a healthy game yields within a handful of lines of CREATE_GAME, so a few hundred silent ones
    /// after it is already damning; this floor just rules out a stray fragment.
    /// </param>
    /// <param name="window">Minimum event-free time after arming, so one burst cannot trip it alone.</param>
    public LogHealthMonitor(int minLines = 200, TimeSpan? window = null)
    {
        _minLines = minLines;
        _window = window ?? TimeSpan.FromMinutes(3);
    }

    /// <summary>
    /// Record one raw line that produced no events. Returns the alert message exactly once per broken
    /// stretch (latched until events flow again), null otherwise.
    /// </summary>
    public string? OnSilentLine(string rawLine, DateTimeOffset now)
    {
        if (!rawLine.Contains(GameStateMarker, StringComparison.Ordinal))
        {
            return null;
        }

        if (!_armed)
        {
            if (rawLine.Contains(GameStartMarker, StringComparison.Ordinal))
            {
                // A game just started in the raw stream and the parser said nothing — that exact line
                // must yield MatchStarted on a healthy parser. Start the clock.
                _armed = true;
                _armedAt = now;
                _silentLinesSinceArmed = 0;
            }

            return null;
        }

        _silentLinesSinceArmed++;

        if (_latched || _silentLinesSinceArmed < _minLines || now - _armedAt < _window)
        {
            return null;
        }

        _latched = true;
        return $"A game started in the log but nothing parsed ({_silentLinesSinceArmed} GameState lines over " +
               $"{(now - _armedAt).TotalMinutes:F0}+ minutes since an unrecognized game start) — a game patch " +
               "likely changed the log format. Matches will NOT be detected or recorded until the parser " +
               "is updated.";
    }

    /// <summary>Any parsed event proves the pipeline healthy: disarm and clear the latch.</summary>
    public void OnEvents(int count)
    {
        if (count <= 0)
        {
            return;
        }

        Reset();
    }

    /// <summary>Forget everything — called when the watch switches to a new log session/stream, so
    /// state observed on one file never colors judgement of the next.</summary>
    public void Reset()
    {
        _armed = false;
        _silentLinesSinceArmed = 0;
        _latched = false;
    }
}
