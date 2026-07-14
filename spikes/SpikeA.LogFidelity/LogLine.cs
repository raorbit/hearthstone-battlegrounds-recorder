using System.Globalization;

namespace SpikeA.LogFidelity;

/// <summary>
/// A single parsed Hearthstone log line.
///
/// Format (confirmed against the real Power.log):
///   "D HH:mm:ss.fffffff &lt;Source&gt;() - &lt;payload&gt;"
/// e.g. "D 22:00:32.6169628 GameState.DebugPrintPower() -     TAG_CHANGE Entity=GameEntity tag=TURN value=1"
///
/// Every game event is printed twice: once by GameState.DebugPrintPower() (authoritative, real-time,
/// monotonic) and once by PowerTaskList.DebugPrintPower() (animation-delayed replay whose timestamps
/// lag and can appear out of order). This spike parses GameState lines only.
///
/// Timestamps carry no date component, so a rollover detector (see <see cref="DateCursor"/>) is needed.
/// </summary>
public readonly struct LogLine
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
        var timeSpanText = raw.AsSpan(2, tsEnd - 2);
        if (!TimeSpan.TryParseExact(timeSpanText, @"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture, out var tod))
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
/// Seeded from the log folder name (e.g. Hearthstone_2026_07_13_21_56_22 → 2026-07-13 21:56:22).
/// GameState lines are monotonic within a day; when the time-of-day decreases relative to the
/// previous GameState line, midnight has rolled over and the date advances by one.
///
/// Only feed this GameState (authoritative) times — the delayed PowerTaskList stream is not monotonic.
/// </summary>
public sealed class DateCursor
{
    private DateOnly _date;
    private TimeSpan _last;

    public DateCursor(DateOnly seedDate, TimeSpan seedTime)
    {
        _date = seedDate;
        _last = seedTime;
    }

    public DateTime Advance(TimeSpan tod)
    {
        if (tod < _last)
            _date = _date.AddDays(1);
        _last = tod;
        return _date.ToDateTime(TimeOnly.FromTimeSpan(tod));
    }
}
