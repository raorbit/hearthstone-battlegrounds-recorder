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

/// <summary>Why no capture target could be selected.</summary>
internal enum WindowResolutionFailure
{
    None = 0,
    NoEligibleWindow = 1,
    TargetMinimized = 2,
}

/// <summary>A capture candidate together with a specific failure reason when none is safe to use.</summary>
internal sealed record WindowResolution(WindowCandidate? Candidate, WindowResolutionFailure Failure);

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
        => ResolveWithReason(candidates, targetProcessId, mainWindowHandle, titleHint).Candidate;

    /// <summary>
    /// Choose the best non-minimized target and retain enough failure detail for the capture layer
    /// to tell the user to restore Hearthstone instead of reporting a misleading generic miss.
    /// </summary>
    internal static WindowResolution ResolveWithReason(
        IReadOnlyList<WindowCandidate> candidates,
        int targetProcessId,
        nint mainWindowHandle,
        string? titleHint)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        WindowCandidate? best = null;
        int bestScore = Exclude;
        int bestTitleLen = int.MaxValue;
        bool minimizedIdentityMatch = false;

        foreach (var c in candidates)
        {
            int score = ScoreIdentity(c, targetProcessId, mainWindowHandle, titleHint);
            if (score == Exclude)
                continue;

            // A minimized WGC source is known to yield black/frozen output. It is still useful as a
            // diagnostic signal, but it must never become the selected capture target.
            if (c.IsMinimized)
            {
                minimizedIdentityMatch = true;
                continue;
            }

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

        return best is not null
            ? new WindowResolution(best, WindowResolutionFailure.None)
            : new WindowResolution(
                null,
                minimizedIdentityMatch
                    ? WindowResolutionFailure.TargetMinimized
                    : WindowResolutionFailure.NoEligibleWindow);
    }

    /// <summary>Rank one candidate. Returns <see cref="Exclude"/> for windows that must not be recorded.</summary>
    public static int Score(
        WindowCandidate candidate,
        int targetProcessId,
        nint mainWindowHandle,
        string? titleHint)
    {
        int score = ScoreIdentity(candidate, targetProcessId, mainWindowHandle, titleHint);
        return score == Exclude || candidate.IsMinimized ? Exclude : score;
    }

    /// <summary>
    /// Scores identity only. Minimized identity matches remain distinguishable here so
    /// <see cref="ResolveWithReason"/> can return <see cref="WindowResolutionFailure.TargetMinimized"/>.
    /// </summary>
    private static int ScoreIdentity(
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

        // Title hint. When the game's process identity is known (its PID or main-window handle), a
        // title match is only a score tie-breaker among the game's own windows — it must NOT make a
        // foreign-process window eligible on its own. Otherwise a deck-tracker overlay whose title
        // also starts with "Hearthstone", running in another process, would be treated as the game
        // and recorded whenever the game itself has no eligible window. Only when no process identity
        // is available at all does the title stand in as the sole identity signal.
        bool processIdentityKnown = targetProcessId != 0 || mainWindowHandle != nint.Zero;
        if (!string.IsNullOrEmpty(titleHint) && candidate.Title is { Length: > 0 } title)
        {
            int titleScore =
                title.Equals(titleHint, Ci) ? 200 :
                title.StartsWith(titleHint, Ci) ? 60 :
                title.Contains(titleHint, Ci) ? 30 : 0;
            if (titleScore > 0)
            {
                score += titleScore;
                if (!processIdentityKnown)
                    hasIdentityMatch = true;
            }
        }

        // A window matching NO identity signal (not the game's main window, not owned by the game
        // process, and not carrying the title hint) must never be recorded. Otherwise, when the game
        // has no eligible window, this would return an arbitrary unrelated top-level window — a
        // browser, chat, whatever happens to be open — and the recorder would capture it (a privacy
        // leak). Reject it so Resolve yields null and the caller raises its "window not found" path.
        if (!hasIdentityMatch)
            return Exclude;

        return score;
    }
}
