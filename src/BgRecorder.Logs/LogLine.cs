using System.Globalization;

namespace BgRecorder.Logs;

/// <summary>
/// A single parsed Hearthstone Power.log line.
///
/// Format (confirmed against the real log, Spike A):
///   "D HH:mm:ss.fffffff &lt;Source&gt;() - &lt;payload&gt;"
/// e.g. "D 22:00:32.6169628 GameState.DebugPrintPower() -     TAG_CHANGE Entity=GameEntity tag=TURN value=1"
///
/// Every game event is printed twice: once by GameState.DebugPrint* (authoritative, real-time, monotonic)
/// and once by PowerTaskList.DebugPrint* (an animation-delayed replay whose timestamps lag and can appear
/// out of order). Only the GameState stream is consumed. Timestamps carry no date component, so absolute
/// wall-clock is reconstructed by <see cref="DateCursor"/>.
/// </summary>
internal readonly struct LogLine
{
    /// <summary>Time-of-day parsed from the line (no date).</summary>
    public TimeSpan TimeOfDay { get; }

    /// <summary>The "Source.Method" token, e.g. "GameState.DebugPrintPower".</summary>
    public string Source { get; }

    /// <summary>Everything after " - ", with leading indentation trimmed.</summary>
    public string Payload { get; }

    public LogLine(TimeSpan timeOfDay, string source, string payload)
    {
        TimeOfDay = timeOfDay;
        Source = source;
        Payload = payload;
    }

    public bool IsGameStatePower => Source == "GameState.DebugPrintPower";
    public bool IsGameStateGame => Source == "GameState.DebugPrintGame";
    public bool IsGameState => Source.StartsWith("GameState.DebugPrint", StringComparison.Ordinal);

    /// <summary>
    /// Parse a raw line. Returns false for blank lines or lines that do not match the
    /// "D HH:mm:ss.fffffff Source() - payload" shape (those are ignored by callers).
    /// </summary>
    public static bool TryParse(string raw, out LogLine line)
    {
        line = default;
        if (string.IsNullOrEmpty(raw) || raw.Length < 20 || raw[0] != 'D' || raw[1] != ' ')
            return false;

        // Time token spans indices 2.. up to the next space.
        int tsEnd = raw.IndexOf(' ', 2);
        if (tsEnd < 0) return false;
        var timeText = raw.AsSpan(2, tsEnd - 2);
        if (!TimeSpan.TryParseExact(timeText, @"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture, out var tod))
            return false;

        // Source token spans from tsEnd+1 up to "() - ".
        int marker = raw.IndexOf("() - ", tsEnd, StringComparison.Ordinal);
        if (marker < 0) return false;
        string source = raw.Substring(tsEnd + 1, marker - (tsEnd + 1));
        string payload = raw[(marker + 5)..].TrimStart();

        line = new LogLine(tod, source, payload);
        return true;
    }
}

/// <summary>
/// Reconstructs absolute timestamps from date-less log times.
///
/// Seeded from the session log folder name (e.g. Hearthstone_2026_07_13_21_56_22 → 2026-07-13 21:56:22).
/// GameState lines are monotonic within a day; when the time-of-day decreases relative to the previous
/// GameState line, midnight has rolled over and the date advances by one. Feed it GameState times only —
/// the delayed PowerTaskList stream is not monotonic and would trigger spurious rollovers.
/// </summary>
internal sealed class DateCursor
{
    // A midnight rollover shows up as a LARGE backward jump in time-of-day: consecutive GameState lines are
    // milliseconds apart, so the only backward steps that occur are a DST fall-back rewind (at most the DST
    // delta, <= 1h) or a benign out-of-order line (sub-second) — neither of which crosses midnight — versus a
    // real wrap (~24h back for consecutive lines; many hours from the session seed). A threshold well above
    // the 1h DST delta and far below any real wrap cleanly separates the two.
    //
    // Residual limitation: a single day is never spanned by more than one wrap between two adjacent lines, and
    // this heuristic assumes any backward jump smaller than the threshold is NOT a date change. A pathological
    // gap where the game emits no GameState line for many hours across midnight AND the first post-midnight
    // line is under the threshold below the pre-midnight line cannot occur here (the seed→first-line and
    // consecutive-line cases both produce far larger jumps), so the conservative rule is safe for this corpus.
    private static readonly TimeSpan RolloverThreshold = TimeSpan.FromHours(2);

    private DateOnly _date;
    private TimeSpan _last;

    public DateCursor(DateOnly seedDate, TimeSpan seedTime)
    {
        _date = seedDate;
        _last = seedTime;
    }

    public DateTime Advance(TimeSpan tod)
    {
        if (_last - tod >= RolloverThreshold)
            _date = _date.AddDays(1);
        _last = tod;
        return _date.ToDateTime(TimeOnly.FromTimeSpan(tod));
    }
}
