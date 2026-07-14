namespace BgRecorder.Capture;

/// <summary>
/// A recordable top-level window, reduced to just what window selection needs.
/// Decoupled from ScreenRecorderLib's <c>RecordableWindow</c> so the scoring logic is
/// pure and unit-testable without the native capture library.
/// </summary>
/// <param name="Handle">The window's HWND.</param>
/// <param name="Title">The window title, if any.</param>
/// <param name="OwningProcessId">PID that owns the window (from GetWindowThreadProcessId); 0 if unknown.</param>
/// <param name="IsValid">Whether the capture library considers the window recordable.</param>
/// <param name="IsMinimized">Whether the window is minimized (capturing it yields black frames).</param>
public sealed record WindowCandidate(
    nint Handle,
    string? Title,
    int OwningProcessId,
    bool IsValid,
    bool IsMinimized);

/// <summary>
/// Picks the window to record. Both the game and any running deck-tracker overlay carry a
/// title containing "Hearthstone", so a naive substring match grabs the wrong (tiny, transparent)
/// overlay window. Because the caller already resolved the game's process, this resolver binds to
/// the game *process's* window — its main window when known, otherwise any window owned by that
/// process — and only falls back to the title hint. Ported and hardened from Spike B.
/// </summary>
public static class WindowResolver
{
    private const StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    /// <summary>Sentinel score marking a candidate that must never be recorded.</summary>
    private const int Exclude = int.MinValue;

    /// <summary>
    /// Choose the best window to record, or <c>null</c> if none is recordable.
    /// </summary>
    /// <param name="candidates">All recordable top-level windows currently reported.</param>
    /// <param name="targetProcessId">PID of the game process (from <see cref="Core.Capture.RecordingTarget"/>).</param>
    /// <param name="mainWindowHandle">The game process's main window handle, or <see cref="nint.Zero"/> if unknown.</param>
    /// <param name="titleHint">Window-title hint used only as a tie-breaker / last resort.</param>
    public static WindowCandidate? Resolve(
        IReadOnlyList<WindowCandidate> candidates,
        int targetProcessId,
        nint mainWindowHandle,
        string? titleHint)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        WindowCandidate? best = null;
        int bestScore = Exclude;
        int bestTitleLen = int.MaxValue;

        foreach (var c in candidates)
        {
            int score = Score(c, targetProcessId, mainWindowHandle, titleHint);
            if (score == Exclude)
                continue;

            int titleLen = c.Title?.Length ?? int.MaxValue;
            // Higher score wins; on a tie the shorter title wins ("Hearthstone" beats
            // "Hearthstone Deck Tracker"); handle value is the final deterministic tie-break.
            if (score > bestScore
                || (score == bestScore && titleLen < bestTitleLen)
                || (score == bestScore && titleLen == bestTitleLen && best is not null && c.Handle > best.Handle))
            {
                best = c;
                bestScore = score;
                bestTitleLen = titleLen;
            }
        }

        return best;
    }

    /// <summary>Rank one candidate. Returns <see cref="Exclude"/> for windows that must not be recorded.</summary>
    public static int Score(
        WindowCandidate candidate,
        int targetProcessId,
        nint mainWindowHandle,
        string? titleHint)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!candidate.IsValid)
            return Exclude;

        int score = 0;
        bool hasIdentityMatch = false;

        // The game process's own main window is the definitive target.
        if (mainWindowHandle != nint.Zero && candidate.Handle == mainWindowHandle)
        {
            score += 4000;
            hasIdentityMatch = true;
        }

        // Any window owned by the game process beats a same-titled overlay from another process.
        if (targetProcessId != 0 && candidate.OwningProcessId == targetProcessId)
        {
            score += 1000;
            hasIdentityMatch = true;
        }

        // Title hint is only a tie-breaker / fallback when process info is unavailable.
        if (!string.IsNullOrEmpty(titleHint) && candidate.Title is { Length: > 0 } title)
        {
            if (title.Equals(titleHint, Ci)) { score += 200; hasIdentityMatch = true; }
            else if (title.StartsWith(titleHint, Ci)) { score += 60; hasIdentityMatch = true; }
            else if (title.Contains(titleHint, Ci)) { score += 30; hasIdentityMatch = true; }
        }

        // A window matching NO identity signal (not the game's main window, not owned by the game
        // process, and not carrying the title hint) must never be recorded. Otherwise, when the game
        // has no eligible window, this would return an arbitrary unrelated top-level window — a
        // browser, chat, whatever happens to be open — and the recorder would capture it (a privacy
        // leak). Reject it so Resolve yields null and the caller raises its "window not found" path.
        if (!hasIdentityMatch)
            return Exclude;

        // Never prefer a minimized window: capturing it produces black frames. This penalty only
        // ranks a minimized identity match below a live one; it can drive the score to zero or below
        // but never re-excludes it, so a minimized-but-present game window still beats nothing at all.
        if (candidate.IsMinimized)
            score -= 5000;

        return score;
    }
}
