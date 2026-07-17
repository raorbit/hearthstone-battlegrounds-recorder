namespace BgRecorder.Logs;

/// <summary>
/// The game-patch tripwire (plan risk #2): raises one alert when the live log is clearly carrying
/// game traffic that the parser no longer understands — a sustained run of <c>GameState.DebugPrint</c>
/// lines yielding zero parsed events.
///
/// Why this cannot false-positive on ordinary play: GameState lines are only written while a game (any
/// mode) is in progress, and every game's very first lines include <c>CREATE_GAME</c>, which parses to
/// <see cref="Core.Events.MatchStarted"/> — so a healthy parser always produces an event within a few
/// lines of GameState traffic starting. Menus and non-game log noise contain no GameState lines and
/// can idle forever without tripping anything. The marker is a raw substring match on the logger name,
/// deliberately independent of the line format the parser expects — a patch that reshapes the line
/// prefix breaks the parser but not this detector.
/// </summary>
public sealed class LogHealthMonitor
{
    private const string GameStateMarker = "GameState.DebugPrint";

    private readonly int _minLines;
    private readonly TimeSpan _window;

    private int _unparsedGameStateLines;
    private DateTimeOffset _windowStart;
    private bool _latched;

    /// <param name="minLines">
    /// GameState lines that must accumulate, event-free, before alerting. A real match produces
    /// thousands; a couple hundred rules out stray fragments while still alerting within the first
    /// minute of a broken match.
    /// </param>
    /// <param name="window">Minimum event-free duration, so a burst alone cannot trip it.</param>
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

        if (_unparsedGameStateLines == 0)
        {
            _windowStart = now;
        }

        _unparsedGameStateLines++;

        if (_latched || _unparsedGameStateLines < _minLines || now - _windowStart < _window)
        {
            return null;
        }

        _latched = true;
        return $"The game is writing match traffic ({_unparsedGameStateLines} GameState lines over " +
               $"{(now - _windowStart).TotalMinutes:F0}+ minutes) but none of it parses — a game patch " +
               "likely changed the log format. Matches will NOT be detected or recorded until the parser " +
               "is updated.";
    }

    /// <summary>Any parsed event proves the pipeline healthy: reset the window and clear the latch.</summary>
    public void OnEvents(int count)
    {
        if (count <= 0)
        {
            return;
        }

        _unparsedGameStateLines = 0;
        _latched = false;
    }
}
