using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenRecorderLib;

namespace BgRecorder.Capture.Internal;

/// <summary>Bridges ScreenRecorderLib's window enumeration into the pure <see cref="WindowCandidate"/> model.</summary>
internal static class NativeWindows
{
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    /// <summary>Enumerate recordable windows and tag each with its owning process id.</summary>
    public static IReadOnlyList<WindowCandidate> Enumerate()
    {
        var windows = Recorder.GetWindows();
        var result = new List<WindowCandidate>(windows.Count);

        foreach (var w in windows)
        {
            int pid = GetWindowThreadProcessId(w.Handle, out uint p) == 0 ? 0 : (int)p;
            result.Add(new WindowCandidate(
                Handle: w.Handle,
                Title: w.Title,
                OwningProcessId: pid,
                IsValid: w.IsValidWindow(),
                IsMinimized: w.IsMinmimized()));
        }

        return result;
    }

    /// <summary>The main window handle of a process, or <see cref="nint.Zero"/> if it can't be read.</summary>
    public static nint MainWindowHandleOf(int processId)
    {
        try
        {
            using var proc = Process.GetProcessById(processId);
            return proc.MainWindowHandle;
        }
        catch
        {
            return nint.Zero;
        }
    }
}
