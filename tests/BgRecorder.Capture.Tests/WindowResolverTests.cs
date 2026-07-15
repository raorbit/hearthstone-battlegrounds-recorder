using BgRecorder.Capture;
using Xunit;

namespace BgRecorder.Capture.Tests;

public sealed class WindowResolverTests
{
    private const int GamePid = 1000;
    private const int TrackerPid = 2000;
    private static readonly nint GameMainHandle = 0x1111;

    private static WindowCandidate Game(nint handle = 0, string title = "Hearthstone", bool minimized = false) =>
        new(handle == 0 ? GameMainHandle : handle, title, GamePid, IsValid: true, IsMinimized: minimized);

    private static WindowCandidate TrackerOverlay(string title) =>
        new(0x9999, title, TrackerPid, IsValid: true, IsMinimized: false);

    [Fact]
    public void Picks_game_main_window_over_same_titled_tracker_overlay()
    {
        var candidates = new List<WindowCandidate>
        {
            TrackerOverlay("Hearthstone Deck Tracker"),
            TrackerOverlay("Hearthstone"),                 // overlay that steals the exact title
            Game(GameMainHandle, "Hearthstone"),
        };

        var pick = WindowResolver.Resolve(candidates, GamePid, GameMainHandle, "Hearthstone");

        Assert.NotNull(pick);
        Assert.Equal(GameMainHandle, pick!.Handle);
        Assert.Equal(GamePid, pick.OwningProcessId);
    }

    [Fact]
    public void Prefers_process_main_window_over_other_windows_of_the_same_process()
    {
        var secondaryGameWindow = new WindowCandidate(0x2222, "Hearthstone", GamePid, IsValid: true, IsMinimized: false);
        var candidates = new List<WindowCandidate>
        {
            secondaryGameWindow,
            Game(GameMainHandle, "Hearthstone"),
        };

        var pick = WindowResolver.Resolve(candidates, GamePid, GameMainHandle, "Hearthstone");

        Assert.Equal(GameMainHandle, pick!.Handle);
    }

    [Fact]
    public void Never_returns_an_invalid_window()
    {
        var candidates = new List<WindowCandidate>
        {
            new(GameMainHandle, "Hearthstone", GamePid, IsValid: false, IsMinimized: false),
        };

        var pick = WindowResolver.Resolve(candidates, GamePid, GameMainHandle, "Hearthstone");

        Assert.Null(pick);
    }

    [Fact]
    public void Returns_null_when_no_candidates()
    {
        var pick = WindowResolver.Resolve([], GamePid, GameMainHandle, "Hearthstone");
        Assert.Null(pick);
    }

    [Fact]
    public void Falls_back_to_pid_when_main_handle_unknown()
    {
        // Main window handle not resolvable (0), but a window owned by the game process exists.
        var owned = new WindowCandidate(0x3333, "Hearthstone", GamePid, IsValid: true, IsMinimized: false);
        var candidates = new List<WindowCandidate>
        {
            TrackerOverlay("Hearthstone Deck Tracker"),
            owned,
        };

        var pick = WindowResolver.Resolve(candidates, GamePid, mainWindowHandle: 0, "Hearthstone");

        Assert.Equal(owned.Handle, pick!.Handle);
    }

    [Fact]
    public void Falls_back_to_title_when_process_info_unavailable()
    {
        // No PID/handle match at all (targetPid 0, handle 0): title hint is the only signal.
        var candidates = new List<WindowCandidate>
        {
            new(0x4444, "Some Other App", 4444, IsValid: true, IsMinimized: false),
            new(0x5555, "Hearthstone", 5555, IsValid: true, IsMinimized: false),
        };

        var pick = WindowResolver.Resolve(candidates, targetProcessId: 0, mainWindowHandle: 0, "Hearthstone");

        Assert.Equal(0x5555, pick!.Handle);
    }

    [Fact]
    public void Shorter_title_wins_a_score_tie()
    {
        // Both owned by the game process, neither is the main handle → tie on score; shorter title wins.
        var candidates = new List<WindowCandidate>
        {
            new(0x6666, "Hearthstone Secondary", GamePid, IsValid: true, IsMinimized: false),
            new(0x7777, "Hearthstone", GamePid, IsValid: true, IsMinimized: false),
        };

        var pick = WindowResolver.Resolve(candidates, GamePid, mainWindowHandle: 0, "Hearthstone");

        Assert.Equal(0x7777, pick!.Handle);
    }

    [Fact]
    public void Minimized_game_window_scores_below_a_non_minimized_game_window()
    {
        var minimizedMain = new WindowCandidate(GameMainHandle, "Hearthstone", GamePid, IsValid: true, IsMinimized: true);
        var liveSecondary = new WindowCandidate(0x8888, "Hearthstone", GamePid, IsValid: true, IsMinimized: false);
        var candidates = new List<WindowCandidate> { minimizedMain, liveSecondary };

        var pick = WindowResolver.Resolve(candidates, GamePid, GameMainHandle, "Hearthstone");

        Assert.Equal(liveSecondary.Handle, pick!.Handle);
    }

    [Fact]
    public void Returns_null_when_no_window_matches_process_or_title()
    {
        // The game has no eligible window; only unrelated top-level windows exist (browser, chat).
        // None matches the main handle, the game PID, or the title hint. Recording one of these would
        // be a privacy leak, so the resolver must return null rather than an arbitrary window.
        var candidates = new List<WindowCandidate>
        {
            new(0xAAAA, "Inbox - Chrome", 4444, IsValid: true, IsMinimized: false),
            new(0xBBBB, "Discord", 5555, IsValid: true, IsMinimized: false),
            new(0xCCCC, null, 6666, IsValid: true, IsMinimized: false),
        };

        var pick = WindowResolver.Resolve(candidates, GamePid, GameMainHandle, "Hearthstone");

        Assert.Null(pick);
    }

    [Fact]
    public void Rejects_foreign_process_hearthstone_titled_window_when_the_game_window_is_absent()
    {
        // The game process is known (PID + main handle) but has no eligible window right now
        // (startup / loading / a fullscreen transition). A deck-tracker window from ANOTHER process
        // still carries a "Hearthstone…" title — it must not be treated as the game and recorded.
        // Resolve returns null so the caller raises its "window not found" path instead.
        var tracker = TrackerOverlay("Hearthstone Deck Tracker");

        var pick = WindowResolver.Resolve([tracker], GamePid, GameMainHandle, "Hearthstone");

        Assert.Null(pick);
    }

    [Fact]
    public void Foreign_titled_window_is_excluded_when_process_identity_is_known()
    {
        // Direct scoring check: with the game's PID/handle known, a same-titled foreign-process
        // window earns no identity credit from its title and is rejected outright.
        var tracker = TrackerOverlay("Hearthstone Deck Tracker");

        int score = WindowResolver.Score(tracker, GamePid, GameMainHandle, "Hearthstone");

        Assert.Equal(int.MinValue, score);
    }

    [Fact]
    public void Scores_a_window_with_no_identity_signal_as_excluded()
    {
        // Direct check on the scoring floor: a window matching nothing is rejected, not scored zero.
        var unrelated = new WindowCandidate(0xDDDD, "Notepad", 7777, IsValid: true, IsMinimized: false);

        int score = WindowResolver.Score(unrelated, GamePid, GameMainHandle, "Hearthstone");

        Assert.Equal(int.MinValue, score);
    }

    [Fact]
    public void Minimized_game_window_is_chosen_over_an_unrelated_window()
    {
        // Regression: previously the -5000 minimized penalty cancelled the identity score to zero,
        // letting an unrelated zero-scored window win the tie. Now the unrelated window is excluded,
        // so a present-but-minimized game window is still selected over something unrelated.
        var minimizedGame = new WindowCandidate(GameMainHandle, "Hearthstone", GamePid, IsValid: true, IsMinimized: true);
        var unrelated = new WindowCandidate(0xEEEE, "Some Other App", 4444, IsValid: true, IsMinimized: false);
        var candidates = new List<WindowCandidate> { unrelated, minimizedGame };

        var pick = WindowResolver.Resolve(candidates, GamePid, GameMainHandle, "Hearthstone");

        Assert.Equal(GameMainHandle, pick!.Handle);
    }
}
