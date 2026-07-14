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
}
