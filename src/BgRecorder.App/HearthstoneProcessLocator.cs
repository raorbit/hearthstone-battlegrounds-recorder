using System.Diagnostics;
using BgRecorder.Core.Capture;
using BgRecorder.Session;

namespace BgRecorder.App;

/// <summary>
/// Production <see cref="IGameProcessLocator"/>: resolves the running Hearthstone process by
/// its well-known process name. Only the PID and a title hint are produced here; binding the
/// actual capture window (and rejecting same-titled tracker overlays) is the capture layer's
/// job via its process-bound window resolver.
/// </summary>
internal sealed class HearthstoneProcessLocator : IGameProcessLocator
{
    private const string ProcessName = "Hearthstone";

    public RecordingTarget? FindGame()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        try
        {
            foreach (var process in processes)
            {
                if (!process.HasExited)
                {
                    return new RecordingTarget(process.Id, ProcessName);
                }
            }
            return null;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
