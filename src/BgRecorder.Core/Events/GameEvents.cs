namespace BgRecorder.Core.Events;

/// <summary>
/// Events emitted by the log pipeline, in log order. Timestamps are wall-clock,
/// reconstructed from the log's time-of-day plus a date cursor (Power.log lines carry no date).
/// </summary>
public abstract record GameEvent(DateTimeOffset Timestamp);

/// <summary>The watcher switched to a (new) session log folder — game start or restart.</summary>
public sealed record LogSessionChanged(DateTimeOffset Timestamp, string LogDirectory) : GameEvent(Timestamp);

/// <summary>CREATE_GAME seen. GameType arrives slightly later via DebugPrintGame.</summary>
public sealed record MatchStarted(DateTimeOffset Timestamp) : GameEvent(Timestamp);

/// <summary>GameType resolved. Non-BG matches must be ignored by the coordinator.</summary>
public sealed record GameTypeResolved(DateTimeOffset Timestamp, BgGameType GameType) : GameEvent(Timestamp);

/// <summary>The local player's current hero entity card id (may fire again on mulligan swap).</summary>
public sealed record LocalHeroResolved(DateTimeOffset Timestamp, string HeroCardId) : GameEvent(Timestamp);

/// <summary>GameEntity TURN change. TavernTurn = (RawTurn + 1) / 2.</summary>
public sealed record TurnStarted(DateTimeOffset Timestamp, int RawTurn, int TavernTurn) : GameEvent(Timestamp);

/// <summary>Combat phase begins (even raw-TURN transition; coincides with BOARD_VISUAL_STATE=2).</summary>
public sealed record CombatStarted(DateTimeOffset Timestamp, int TavernTurn) : GameEvent(Timestamp);

/// <summary>PLAYER_LEADERBOARD_PLACE change for the local player's hero.</summary>
public sealed record PlacementChanged(DateTimeOffset Timestamp, int Place) : GameEvent(Timestamp);

/// <summary>
/// Match over: terminal PLAYSTATE and/or STATE=COMPLETE. Truncated means the log ended
/// (or a new session folder appeared) without a terminal state — client restart or crash.
/// </summary>
public sealed record MatchEnded(DateTimeOffset Timestamp, int? FinalPlace, PlayState PlayState, bool Truncated) : GameEvent(Timestamp);

public enum BgGameType
{
    NotBattlegrounds = 0,
    Solo = 1,
    Duos = 2,
}

public enum PlayState
{
    Unknown = 0,
    Won = 1,
    Lost = 2,
    Conceded = 3,
}

/// <summary>A running source of game events (log watcher + parser).</summary>
public interface IGameEventSource : IAsyncDisposable
{
    event Action<GameEvent>? EventReceived;

    /// <summary>Begin discovery + tailing. Returns once the watch loop is running.</summary>
    Task StartAsync(CancellationToken ct);
}
