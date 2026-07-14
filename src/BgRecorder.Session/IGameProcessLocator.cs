using BgRecorder.Core.Capture;

namespace BgRecorder.Session;

/// <summary>
/// Resolves the running Hearthstone process/window into a capture target.
/// Lives here (not in Core) because only the session layer needs it; the App wires a
/// real implementation (Spike B's WindowResolver pattern) and tests wire a fake.
/// </summary>
public interface IGameProcessLocator
{
    /// <summary>The running game window, or null when Hearthstone isn't running.</summary>
    RecordingTarget? FindGame();
}
