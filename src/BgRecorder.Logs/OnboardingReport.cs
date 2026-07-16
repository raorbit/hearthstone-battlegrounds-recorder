namespace BgRecorder.Logs;

/// <summary>
/// What onboarding did and what the shell should tell the user: the log.config ensure outcome (null with
/// <see cref="LogConfigError"/> set when the write itself failed), and whether Hearthstone must be
/// restarted to pick a just-made config change up.
/// </summary>
public sealed record OnboardingReport(LogConfigResult? LogConfig, string? LogConfigError, bool GameRestartNeeded)
{
    /// <summary>
    /// The restart rule, kept pure so it is testable: the game reads log.config once at launch, so a
    /// restart prompt is due exactly when the ensure actually changed the file AND the game is running now.
    /// An already-compliant file never needs one; a change with no game running is picked up whenever the
    /// game next starts.
    /// </summary>
    public static OnboardingReport From(LogConfigResult result, bool gameRunning) =>
        new(result, null, result.Outcome is not LogConfigOutcome.AlreadyCompliant && gameRunning);

    /// <summary>The ensure write threw: no outcome to report, and no restart hint can be owed.</summary>
    public static OnboardingReport Failed(string error) => new(null, error, GameRestartNeeded: false);
}
