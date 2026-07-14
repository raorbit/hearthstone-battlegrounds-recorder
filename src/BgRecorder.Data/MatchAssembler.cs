using BgRecorder.Core.Data;
using BgRecorder.Core.Events;

namespace BgRecorder.Data;

/// <summary>
/// Pure reduction of a parsed event stream (plus the finalized recording timeline) into the
/// <see cref="MatchRecord"/> + <see cref="MarkerRecord"/> pair that gets persisted. No I/O, no clock —
/// every value is derived from its inputs so it is fully unit-testable against synthetic streams.
/// </summary>
public sealed class MatchAssembler : IMatchAssembler
{
    public (MatchRecord Match, IReadOnlyList<MarkerRecord> Markers) Assemble(
        IReadOnlyList<GameEvent> events,
        RecordingTimeline? timeline,
        VideoStatus videoStatus)
    {
        ArgumentNullException.ThrowIfNull(events);

        var matchStarted = events.OfType<MatchStarted>().FirstOrDefault();
        var matchEnded = events.OfType<MatchEnded>().LastOrDefault();

        var hero = events.OfType<LocalHeroResolved>().LastOrDefault()?.HeroCardId;
        var gameType = events.OfType<GameTypeResolved>().LastOrDefault()?.GameType ?? BgGameType.NotBattlegrounds;

        var lastPlacement = events.OfType<PlacementChanged>().LastOrDefault()?.Place;
        var place = matchEnded?.FinalPlace ?? lastPlacement;

        var tavernTurns = events.OfType<TurnStarted>().Select(e => e.TavernTurn).DefaultIfEmpty(0).Max();

        // No terminal state seen -> the match was cut off (client restart / crash / log truncation).
        var playState = matchEnded?.PlayState ?? PlayState.Unknown;
        var truncated = matchEnded?.Truncated ?? true;

        var startedAt =
            matchStarted?.Timestamp
            ?? events.FirstOrDefault()?.Timestamp
            ?? timeline?.VideoFirstFrameWallClock
            ?? default;
        var endedAt = matchEnded?.Timestamp;

        var match = new MatchRecord
        {
            StartedAt = startedAt,
            EndedAt = endedAt,
            GameType = gameType,
            HeroCardId = hero,
            Place = place,
            TavernTurns = tavernTurns,
            PlayState = playState,
            Truncated = truncated,
            VideoStatus = videoStatus,
            VideoPath = timeline?.FinalVideoPath,
            VideoSizeBytes = timeline?.SizeBytes,
            VideoDuration = timeline?.Duration,
        };

        var markers = BuildMarkers(events, timeline);
        return (match, markers);
    }

    private static IReadOnlyList<MarkerRecord> BuildMarkers(IReadOnlyList<GameEvent> events, RecordingTimeline? timeline)
    {
        // Markers are offsets into the video; without a recording there is nothing to anchor them to.
        if (timeline is null)
            return [];

        var firstFrame = timeline.VideoFirstFrameWallClock;
        var markers = new List<MarkerRecord>();
        var currentTavernTurn = 0;

        foreach (var e in events)
        {
            switch (e)
            {
                case TurnStarted ts:
                    currentTavernTurn = ts.TavernTurn;
                    markers.Add(new MarkerRecord(0, MarkerKind.TurnStart, OffsetMs(ts.Timestamp, firstFrame), ts.TavernTurn));
                    break;
                case CombatStarted cs:
                    markers.Add(new MarkerRecord(0, MarkerKind.CombatStart, OffsetMs(cs.Timestamp, firstFrame), cs.TavernTurn));
                    break;
                case MatchEnded me:
                    markers.Add(new MarkerRecord(0, MarkerKind.MatchEnd, OffsetMs(me.Timestamp, firstFrame), currentTavernTurn));
                    break;
            }
        }

        return markers;
    }

    /// <summary>Milliseconds from first video frame to the event, clamped to zero (pre-roll events snap to 0).</summary>
    private static long OffsetMs(DateTimeOffset timestamp, DateTimeOffset firstFrame)
    {
        var ms = (long)(timestamp - firstFrame).TotalMilliseconds;
        return ms < 0 ? 0 : ms;
    }
}
